---
name: ui-modal-dialog-overlay-design
description: Designing modal dialog overlays: backdrop blur, focus trapping, ESC key dismiss, animation transitions, and mobile bottom sheet adaptations.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Modal Dialog & Overlay Design

Modal dialogs temporarily interrupt user workflow to focus attention on a critical task, confirmation, or detail inspection.

## Modal Anatomy

- **Backdrop Overlay**: Semi-transparent dark overlay (`rgba(0,0,0,0.6)`) dimming background page content.
- **Modal Container**: Elevated card positioned dead-center on desktop (or converted to a bottom sheet on mobile).
- **Header**: Action title alongside an explicit `✕` close button.
- **Body & Footer**: Content area and primary/secondary button group.

---

## Mobile Bottom Sheet Adaptation CSS

```css
/* Responsive Modal: Center on Desktop, Bottom Sheet on Mobile */
.modal-container {
  position: fixed;
  z-index: 50;
  background: #1e293b;
  border-radius: 16px 16px 0 0;
  bottom: 0;
  left: 0;
  right: 0;
  padding: 24px;
}

@media (min-width: 640px) {
  .modal-container {
    bottom: auto;
    left: 50%;
    top: 50%;
    transform: translate(-50%, -50%);
    width: 100%;
    max-width: 500px;
    border-radius: 16px;
  }
}
```

---

## Verification Checklist

- [ ] Pressing the `ESC` key dismisses the active modal dialog.
- [ ] Clicking outside the modal container on the backdrop closes the overlay.
- [ ] Keyboard focus is trapped inside the open modal dialog.
