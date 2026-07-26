---
name: ui-dashboard-data-visualization
description: Architecting executive dashboards & data visualizations: grid layouts, KPI metric cards, Chart.js/Recharts integration, and high data-density balance.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Dashboard & Data Visualization

Dashboards aggregate complex operational data into digestible visual layouts utilizing KPI summary cards, time-series charts, and data tables.

## Dashboard Grid Layout Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Top Bar / Header Filter & Date Range Picker                │
├───────────────┬───────────────┬───────────────┬─────────────┤
│  KPI Card 1   │  KPI Card 2   │  KPI Card 3   │ KPI Card 4  │
│  Revenue      │  Active Users │  Conversion   │ Churn Rate  │
├───────────────┴───────────────┴───────────────┴─────────────┤
│  Main Time-Series Chart (Recharts / Chart.js)               │
├───────────────────────────────┬─────────────────────────────┤
│  Secondary Category Pie/Bar   │  Recent Transactions Table  │
└───────────────────────────────┴─────────────────────────────┘
```

---

## Recharts Responsive Area Chart Blueprint

```typescript
import { ResponsiveContainer, AreaChart, Area, XAxis, YAxis, Tooltip } from 'recharts';

const data = [
  { date: 'Jan', revenue: 4000 },
  { date: 'Feb', revenue: 6500 },
  { date: 'Mar', revenue: 9800 }
];

export function RevenueChart() {
  return (
    <div className="h-72 w-full bg-slate-900 p-4 rounded-xl border border-slate-800">
      <h3 className="text-sm font-medium text-slate-400 mb-4">Revenue Trend</h3>
      <ResponsiveContainer width="100%" height="85%">
        <AreaChart data={data}>
          <defs>
            <linearGradient id="colorRevenue" x1="0" y1="0" x2="0" y2="1">
              <stop offset="5%" stopColor="#38bdf8" stopOpacity={0.4}/>
              <stop offset="95%" stopColor="#38bdf8" stopOpacity={0}/>
            </linearGradient>
          </defs>
          <XAxis dataKey="date" stroke="#64748b" />
          <YAxis stroke="#64748b" />
          <Tooltip contentStyle={{ backgroundColor: '#0f172a', borderColor: '#334155' }} />
          <Area type="monotone" dataKey="revenue" stroke="#38bdf8" fillOpacity={1} fill="url(#colorRevenue)" />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
}
```

---

## Verification Checklist

- [ ] KPI cards display clear metric values alongside trend percentage indicators ($\uparrow +12\%$).
- [ ] Charts wrapped in `ResponsiveContainer` auto-fit parent container widths.
- [ ] Tooltip overlays provide exact numerical precision on hover.
