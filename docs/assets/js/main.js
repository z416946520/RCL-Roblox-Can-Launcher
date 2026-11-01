(() => {
  const body = document.body;
  const header = document.getElementById("site-header");
  const navToggle = document.getElementById("nav-toggle");
  const navLinks = document.getElementById("nav-links");

  const onScroll = () => {
    if (header) header.classList.toggle("scrolled", window.scrollY > 12);
  };
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  const closeNav = () => {
    body.classList.remove("nav-open");
    navToggle?.setAttribute("aria-expanded", "false");
    navToggle?.setAttribute("aria-label", "展开导航");
  };

  navToggle?.addEventListener("click", () => {
    const open = body.classList.toggle("nav-open");
    navToggle.setAttribute("aria-expanded", String(open));
    navToggle.setAttribute("aria-label", open ? "收起导航" : "展开导航");
  });

  navLinks?.querySelectorAll("a").forEach((link) => {
    link.addEventListener("click", closeNav);
  });

  // Scroll reveal
  const reveals = document.querySelectorAll(".reveal");
  if ("IntersectionObserver" in window) {
    const revealObserver = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add("visible");
            revealObserver.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.12, rootMargin: "0px 0px -40px 0px" }
    );
    reveals.forEach((el) => revealObserver.observe(el));
  } else {
    reveals.forEach((el) => el.classList.add("visible"));
  }

  // Scroll spy
  const navItems = Array.from(document.querySelectorAll(".nav-links a"));
  const sections = ["top", "features", "screens", "download", "faq"]
    .map((id) => document.getElementById(id))
    .filter(Boolean);

  if ("IntersectionObserver" in window && sections.length) {
    const spy = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          const id = entry.target.id;
          navItems.forEach((item) => {
            item.classList.toggle("active", item.getAttribute("href") === `#${id}`);
          });
        });
      },
      { rootMargin: "-35% 0px -55% 0px", threshold: 0 }
    );
    sections.forEach((section) => spy.observe(section));
  }

  // Lightbox
  const lightbox = document.getElementById("lightbox");
  const lightboxImage = document.getElementById("lightbox-image");
  const lightboxClose = document.getElementById("lightbox-close");

  const closeLightbox = () => {
    if (!lightbox) return;
    lightbox.classList.remove("open");
    lightbox.setAttribute("aria-hidden", "true");
    body.style.overflow = "";
  };

  const openLightbox = (src) => {
    if (!lightbox || !lightboxImage) return;
    lightboxImage.src = src;
    lightbox.classList.add("open");
    lightbox.setAttribute("aria-hidden", "false");
    body.style.overflow = "hidden";
  };

  document.querySelectorAll("[data-full]").forEach((item) => {
    item.addEventListener("click", () => openLightbox(item.dataset.full));
    item.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        openLightbox(item.dataset.full);
      }
    });
  });

  lightboxClose?.addEventListener("click", closeLightbox);
  lightbox?.addEventListener("click", (event) => {
    if (event.target === lightbox) closeLightbox();
  });

  // Copy build command
  document.querySelectorAll("[data-copy]").forEach((button) => {
    button.addEventListener("click", async () => {
      const target = document.querySelector(button.dataset.copy);
      if (!target) return;
      const text = target.textContent.trim();
      try {
        await navigator.clipboard.writeText(text);
      } catch {
        const area = document.createElement("textarea");
        area.value = text;
        area.style.position = "fixed";
        area.style.opacity = "0";
        document.body.appendChild(area);
        area.select();
        document.execCommand("copy");
        area.remove();
      }
      const previous = button.textContent;
      button.textContent = "已复制";
      window.setTimeout(() => {
        button.textContent = previous;
      }, 1600);
    });
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closeLightbox();
      closeNav();
    }
  });
})();
