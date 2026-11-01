using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using RCL.Models;

namespace RCL.Core;

public static class InstanceStore
{
    public static ObservableCollection<RclInstance> All { get; } = new();

    public static event Action? Changed;
    public static event Action<RclInstance>? Saved;

    public static RclInstance? Find(Guid id) => All.FirstOrDefault(i => i.Id == id);

    public static void Load()
    {
        AppPaths.Ensure();
        All.Clear();
        try
        {
            foreach (var dir in Directory.GetDirectories(AppPaths.Instances))
            {
                var meta = Path.Combine(dir, "instance.json");
                if (!File.Exists(meta))
                    continue;
                try
                {
                    var inst = JsonSerializer.Deserialize<RclInstance>(File.ReadAllText(meta), JsonOpts.Read);
                    if (inst is null)
                        continue;
                    inst.FastFlags ??= new();
                    if (inst.Id == Guid.Empty)
                        inst.Id = Guid.NewGuid();
                    Directory.CreateDirectory(inst.ModsFolder);
                    Directory.CreateDirectory(inst.ClientSettingsFolder);
                    All.Add(inst);
                }
                catch (Exception ex)
                {
                    Log.Warn($"实例元数据损坏，已跳过 {dir}：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("扫描实例目录失败", ex);
        }

        Sort();
        foreach (var inst in All)
            inst.NotifyAll();
        Changed?.Invoke();
    }

    public static RclInstance Create(string name)
    {
        AppPaths.Ensure();
        var inst = new RclInstance
        {
            Name = UniqueName(name),
            MultiInstance = AppSettings.Current.DefaultMultiInstance,
            IconColor = NextColor()
        };
        Directory.CreateDirectory(inst.Folder);
        Directory.CreateDirectory(inst.ModsFolder);
        Directory.CreateDirectory(inst.ClientSettingsFolder);
        Save(inst);
        All.Add(inst);
        Changed?.Invoke();
        Log.Info("创建实例：" + inst.Name);
        return inst;
    }

    public static RclInstance Duplicate(RclInstance source)
    {
        var copy = new RclInstance
        {
            Name = UniqueName(source.Name + " 副本"),
            Notes = source.Notes,
            VersionPath = source.VersionPath,
            MultiInstance = source.MultiInstance,
            LaunchArgs = source.LaunchArgs,
            IconColor = source.IconColor,
            ApplyMods = source.ApplyMods,
            FastFlags = new ObservableCollection<FastFlagEntry>(
                source.FastFlags.Select(f => new FastFlagEntry { Name = f.Name, Value = f.Value, Enabled = f.Enabled }))
        };

        Directory.CreateDirectory(copy.Folder);
        CopyDirectory(source.ModsFolder, copy.ModsFolder);
        Directory.CreateDirectory(copy.ClientSettingsFolder);
        Save(copy);
        All.Add(copy);
        Changed?.Invoke();
        Log.Info($"复制实例：{source.Name} -> {copy.Name}");
        return copy;
    }

    public static void Delete(RclInstance inst)
    {
        All.Remove(inst);
        try
        {
            if (Directory.Exists(inst.Folder))
                Directory.Delete(inst.Folder, true);
        }
        catch (Exception ex)
        {
            Log.Error("删除实例目录失败", ex);
            throw;
        }
        Changed?.Invoke();
        Log.Info("删除实例：" + inst.Name);
    }

    public static void Save(RclInstance inst)
    {
        try
        {
            Directory.CreateDirectory(inst.Folder);
            Directory.CreateDirectory(inst.ModsFolder);
            Directory.CreateDirectory(inst.ClientSettingsFolder);
            var json = JsonSerializer.Serialize(inst, JsonOpts.Write);
            var tmp = inst.MetaFile + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, inst.MetaFile, true);
            Saved?.Invoke(inst);
        }
        catch (Exception ex)
        {
            Log.Error("保存实例失败：" + inst.Name, ex);
        }
    }

    public static void SaveAll()
    {
        foreach (var inst in All)
            Save(inst);
    }

    public static void Sort()
    {
        var ordered = All.OrderByDescending(i => i.LastPlayed ?? DateTime.MinValue).ThenBy(i => i.Name).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var current = All.IndexOf(ordered[i]);
            if (current != i)
                All.Move(current, i);
        }
    }

    public static string UniqueName(string desired)
    {
        desired = string.IsNullOrWhiteSpace(desired) ? "新实例" : desired.Trim();
        var name = desired;
        int n = 2;
        while (All.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{desired} ({n++})";
        return name;
    }

    private static string NextColor()
    {
        string[] palette = { "#1370F3", "#27AE60", "#F39C12", "#E74C3C", "#8E44AD", "#16A085", "#D35400", "#2C3E50" };
        return palette[All.Count % palette.Length];
    }

    private static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from))
            return;
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), true);
    }
}
