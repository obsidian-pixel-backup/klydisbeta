---
name: ux-feedback-loops-loading-states
description: Designing user feedback loops and loading UI: Skeleton screens vs spinners, progress indicators, toast notification positioning, and subtle status banners.
category: UX & User Experience
author: Klydis Team
version: 2.0.0
---

# UX Feedback Loops & Loading States

Feedback loops keep users informed about system status, turning perception of wait times into engaging, reassuring experiences.

## Loading UI Selection Guide

- **Skeleton Screens**: Best for content cards, dashboards, and page navigation feeds. Skeletons decrease perceived loading time over blank spinners.
- **Spinners**: Best for inline button actions (`Saving...`, `Processing payment...`) taking under 2 seconds.
- **Progress Bars**: Best for multi-step tasks or file upload operations where completion percentage is calculable.

---

## Animated Skeleton Screen Recipe Blueprint

```html
<div class="skeleton-card p-4 bg-slate-900 rounded-xl space-y-3 animate-pulse border border-slate-800">
  <!-- Image placeholder -->
  <div class="h-40 bg-slate-800 rounded-lg w-full"></div>

  <!-- Title line placeholder -->
  <div class="h-4 bg-slate-800 rounded w-3/4"></div>

  <!-- Subtitle placeholder -->
  <div class="h-3 bg-slate-800 rounded w-1/2"></div>
</div>
```

---

## Verification Checklist

- [ ] Page content loading displays skeleton screens instead of blank white spaces.
- [ ] Buttons display inline loading spinners and disable duplicate click events.
- [ ] Toast notifications auto-dismiss after 4-5 seconds and provide a manual dismiss close button.
