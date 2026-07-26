---
name: ui-responsive-adaptive-design
description: Mastering mobile-first responsive design: breakpoint scales, fluid layouts, flexbox/grid adaptation, container queries (`@container`), and device testing.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Responsive & Adaptive Design Architecture

Responsive web design ensures interfaces adapt fluidly across mobile phones, tablets, laptops, and ultra-wide desktop monitors.

## Tailwind & CSS Breakpoint Scale Standard

| Breakpoint | Prefix | Minimum Width | Target Devices |
| :--- | :--- | :--- | :--- |
| **Mobile** | Default | $0\text{px}$ | Smartphones (Portrait) |
| **Tablet** | `sm:` | $640\text{px}$ | Mobile Landscape / Mini Tablets |
| **Medium** | `md:` | $768\text{px}$ | Tablets (Portrait) |
| **Large** | `lg:` | $1024\text{px}$ | Laptops & Desktop Monitors |
| **Extra Large** | `xl:` | $1280\text{px}$ | Large Desktop Displays |

---

## Modern CSS Container Queries (`@container`)

Container queries evaluate parent component container width rather than viewport size:

```css
/* Card parent declares container context */
.card-wrapper {
  container-type: inline-size;
  container-name: card-container;
}

/* Card adapts layout based on IT'S OWN width */
@container card-container (min-width: 400px) {
  .card-content {
    display: flex;
    flex-direction: row;
    align-items: center;
  }
}
```

---

## Verification Checklist

- [ ] Layout is designed Mobile-First (base styles target mobile; breakpoints expand up).
- [ ] Horizontal scrollbars are completely avoided on mobile viewports ($320\text{px}-414\text{px}$).
- [ ] Navigation menu converts gracefully to a mobile drawer/hamburger menu on small screens.
