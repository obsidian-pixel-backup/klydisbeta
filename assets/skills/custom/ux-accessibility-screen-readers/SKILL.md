---
name: ux-accessibility-screen-readers
description: Optimizing screen reader navigation: aria-live region announcements, semantic HTML landmarks, screen reader testing, and accessible name calculation.
category: UX & User Experience
author: Klydis Team
version: 2.0.0
---

# UX Accessibility: Screen Readers & Assistive Tech

Screen reader users navigate web interfaces audibly via semantic landmarks, keyboard shortcuts, and ARIA announcements.

## Essential ARIA Attributes

- `aria-live="polite"`: Announces dynamic content changes (status updates, toast notifications) without interrupting current speech.
- `aria-live="assertive"`: Announces urgent error alerts immediately.
- `aria-expanded="true/false"`: Communicates collapsible accordion / dropdown states.

---

## Live Notification Region Blueprint

```html
<!-- Live Announcement Region for Toast Notifications -->
<div aria-live="polite" aria-atomic="true" class="sr-only">
  <!-- Screen readers audibly read new inner text as it changes -->
  <span id="status-message">Project saved successfully.</span>
</div>
```

---

## HTML5 Semantic Landmarks Architecture

Ensure page layouts utilize semantic HTML tags so screen readers can jump between regions:
- `<header>`: Page banner & navigation.
- `<main>`: Core unique content area.
- `<nav>`: Primary navigation links.
- `<aside>`: Sidebar content.
- `<footer>`: Page footer info.

---

## Verification Checklist

- [ ] Dynamic status changes publish announcements to `aria-live` regions.
- [ ] Icons lacking text labels feature explicit `aria-label` tags.
- [ ] Page layout uses standard HTML5 semantic landmark elements.
