---
name: ui-table-data-grid-design
description: Designing high-density data tables and grids: sticky headers, column sorting/filtering UI, row selection, pagination controls, and hover highlights.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Data Table & Grid Design

Data tables organize high-density information into rows and columns, providing sorting, filtering, row selection, and inline actions.

## Data Table Layout Architecture

```html
<div class="table-container overflow-x-auto rounded-xl border border-slate-800">
  <table class="w-full text-left text-sm text-slate-300">
    <!-- Sticky Header -->
    <thead class="sticky top-0 bg-slate-900 text-xs uppercase text-slate-400 border-b border-slate-800">
      <tr>
        <th class="p-4"><input type="checkbox" aria-label="Select all rows" /></th>
        <th class="p-4 cursor-pointer">User Name ↕</th>
        <th class="p-4">Role</th>
        <th class="p-4">Status</th>
        <th class="p-4 text-right">Actions</th>
      </tr>
    </thead>
    <!-- Table Body -->
    <tbody class="divide-y divide-slate-800/60 bg-slate-950">
      <tr class="hover:bg-slate-900/50 transition-colors">
        <td class="p-4"><input type="checkbox" /></td>
        <td class="p-4 font-medium text-white">Alice Smith</td>
        <td class="p-4">Admin</td>
        <td class="p-4"><span class="badge-success">Active</span></td>
        <td class="p-4 text-right"><button class="btn-ghost">Edit</button></td>
      </tr>
    </tbody>
  </table>
</div>
```

---

## Verification Checklist

- [ ] Table header (`<thead>`) remains pinned (sticky) during vertical scroll.
- [ ] Table rows highlight visibly on hover (`hover:bg-slate-900`).
- [ ] Numeric columns align right; text columns align left.
- [ ] Horizontal scroll container handles overflow on small screens cleanly.
