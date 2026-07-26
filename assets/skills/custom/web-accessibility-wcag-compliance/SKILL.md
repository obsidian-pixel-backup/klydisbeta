---
name: web-accessibility-wcag-compliance
description: Achieving WCAG 2.2 AAA accessibility compliance: ARIA attributes, semantic HTML5, keyboard focus trap management, and screen reader testing.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Web Accessibility (WCAG 2.2 AAA) Compliance

Web accessibility ensures that applications are fully usable by individuals with visual, auditory, motor, or cognitive impairments.

## Core WCAG Principles (POUR)

1. **Perceivable**: Text alternatives for non-text content, minimum color contrast ($4.5:1$ for normal text).
2. **Operable**: All functionality accessible via keyboard interface; no focus traps.
3. **Understandable**: Predictable UI navigation and human-readable error instructions.
4. **Robust**: Compatibility with assistive technologies (screen readers).

---

## Accessible Modal Dialog Pattern Blueprint

```typescript
import React, { useEffect, useRef } from 'react';

export function AccessibleModal({ isOpen, onClose, title, children }: ModalProps) {
  const modalRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    if (isOpen) {
      document.addEventListener('keydown', handleKeyDown);
      modalRef.current?.focus();
    }
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, onClose]);

  if (!isOpen) return null;

  return (
    <div className="backdrop" onClick={onClose}>
      <div
        ref={modalRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="modal-title"
        tabIndex={-1}
        className="modal-content"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 id="modal-title">{title}</h2>
        {children}
        <button onClick={onClose} aria-label="Close modal">✕</button>
      </div>
    </div>
  );
}
```

---

## Verification Checklist

- [ ] Interactive elements (`button`, `a`, `input`) are focusable with visible outline indicators.
- [ ] Images have descriptive `alt` text tags (or `alt=""` if purely decorative).
- [ ] Form inputs are bound to explicit `<label htmlFor="...">` tags.
- [ ] Automated accessibility audits (`axe-core`, Lighthouse) pass with 0 critical violations.
