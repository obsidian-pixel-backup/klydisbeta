---
name: ui-iconography-visual-assets
description: Selecting and integrating icon systems: Lucide Icons, Heroicons, SVG optimization (`svgo`), icon sizing scales, and visual alignment.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Iconography & Visual Assets

Icons enhance scannability and convey action intent cleanly when integrated into buttons, input controls, and navigation items.

## Icon System Best Practices

1. **Use Single Vector Icon Library**: Stick to one coherent icon family (e.g., Lucide Icons or Heroicons) to maintain consistent stroke weights.
2. **Inline SVG over Font Icons**: Inline SVGs prevent flash-of-unstyled-text and render crisp on retina displays.
3. **Size Scaling**: Standardize icon size tokens ($16\text{px}, 20\text{px}, 24\text{px}$).

---

## Lucide React Integration Blueprint

```typescript
import { Search, Bell, Settings, User } from 'lucide-react';

export function NavigationIcons() {
  return (
    <div class="flex gap-4">
      <button aria-label="Search"><Search size={20} className="text-slate-400 hover:text-white" /></button>
      <button aria-label="Notifications"><Bell size={20} className="text-slate-400 hover:text-white" /></button>
      <button aria-label="Settings"><Settings size={20} className="text-slate-400 hover:text-white" /></button>
    </div>
  );
}
```

---

## Verification Checklist

- [ ] Decorative icons are hidden from screen readers using `aria-hidden="true"`.
- [ ] Icon-only buttons feature explicit `aria-label` attribute descriptions.
- [ ] SVGs are optimized with `svgo` to remove unnecessary vector metadata bytes.
