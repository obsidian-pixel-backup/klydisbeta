---
name: ui-form-input-control-design
description: Designing UI form controls: input states (default, hover, focus-visible, error, disabled), custom checkboxes/radios, floating labels, and select pickers.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Form Input & Control Design

Form inputs are the primary control interface for user data entry. They require unambiguous focus rings, accessible error states, and clean visual styling.

## Essential Input Component States

1. **Default**: Neutral border color (`#334155`), clean background.
2. **Hover**: Slightly highlighted border (`#475569`).
3. **Focus-Visible**: High-contrast focus outline ring (`2px solid #38bdf8`) with offset.
4. **Error**: Red accent border (`#ef4444`) with attached error helper text.
5. **Disabled**: Muted background, decreased opacity ($50\%$), `cursor: not-allowed`.

---

## Production Styled Input Blueprint (CSS)

```css
.input-control {
  width: 100%;
  height: 40px;
  padding: 0 12px;
  background-color: #0f172a;
  border: 1px solid #334155;
  border-radius: 8px;
  color: #f8fafc;
  font-size: 14px;
  transition: border-color 150ms ease, box-shadow 150ms ease;
}

.input-control:hover:not(:disabled) {
  border-color: #475569;
}

.input-control:focus-visible {
  outline: none;
  border-color: #38bdf8;
  box-shadow: 0 0 0 3px rgba(56, 189, 248, 0.25);
}

.input-control[aria-invalid="true"] {
  border-color: #ef4444;
}

.input-control[aria-invalid="true"]:focus-visible {
  box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.25);
}
```

---

## Verification Checklist

- [ ] Inputs feature prominent focus indicator rings for keyboard accessibility.
- [ ] Error states display a red border accompanied by explicit helper text below the field.
- [ ] Form controls maintain a minimum touch target height of $44\text{px}$ on mobile.
