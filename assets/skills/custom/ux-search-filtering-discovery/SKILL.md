---
name: ux-search-filtering-discovery
description: Designing global search, autocomplete dropdowns, multi-facet filtering panels, search query highlighting, zero-result handling, and discovery UX.
category: UX & User Experience
author: Klydis Team
version: 2.0.0
---

# UX Search, Filtering & Discovery

Search and filtering patterns help users locate specific records quickly across large datasets.

## Core Discovery UX Components

1. **Global Search Palette (`Cmd+K`)**: Instant modal search overlay accessible anywhere via keyboard shortcut.
2. **Faceted Filter Panel**: Multi-select sidebar controls allowing granular category filtering.
3. **Highlighted Query Matches**: Highlight matching query terms within search result items.

---

## Global Search Overlay Component Blueprint (`Cmd+K`)

```typescript
import React, { useEffect, useState } from 'react';

export function CommandKSearch() {
  const [isOpen, setIsOpen] = useState(false);
  const [query, setQuery] = useState('');

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
        e.preventDefault();
        setIsOpen((prev) => !prev);
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, []);

  if (!isOpen) return null;

  return (
    <div className="search-modal-backdrop" onClick={() => setIsOpen(false)}>
      <div className="search-modal-box" onClick={(e) => e.stopPropagation()}>
        <input
          type="text"
          placeholder="Search documentation, components, files... (Cmd+K)"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          autoFocus
          className="search-input"
        />
        <div className="search-results-list">
          {/* Render query matches */}
        </div>
      </div>
    </div>
  );
}
```

---

## Verification Checklist

- [ ] Search input supports instant debounce searching ($250\text{ms}$).
- [ ] Zero search result screens offer helpful suggestions (check spelling, clear filters).
- [ ] Filter chips display clear "Clear All Filters" button controls.
