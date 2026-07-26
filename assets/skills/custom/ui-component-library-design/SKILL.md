---
name: ui-component-library-design
description: Building production component libraries: atomic design, variant API design, component composition, prop interfaces, and accessibility states.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Component Library Design Architecture

A robust component library enforces design consistency while allowing flexibility through clear component composition APIs.

## Atomic Component Taxonomy

1. **Atoms**: Primitive building blocks (Button, Input, Badge, Spinner).
2. **Molecules**: Combinations of atoms (SearchInput, FormField, UserAvatarCard).
3. **Organisms**: Complex interface sections (Header, Sidebar, DataTable, Modal).

---

## Production Button Component Blueprint (TypeScript + React)

```typescript
import React from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils';

const buttonVariants = cva(
  "inline-flex items-center justify-center rounded-lg font-medium transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:opacity-50 disabled:pointer-events-none",
  {
    variants: {
      variant: {
        primary: "bg-blue-600 hover:bg-blue-700 text-white shadow-md",
        secondary: "bg-slate-800 hover:bg-slate-700 text-slate-200 border border-slate-700",
        ghost: "hover:bg-slate-800 text-slate-300"
      },
      size: {
        sm: "h-8 px-3 text-xs",
        md: "h-10 px-4 text-sm",
        lg: "h-12 px-6 text-base"
      }
    },
    defaultVariants: { variant: 'primary', size: 'md' }
  }
);

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  isLoading?: boolean;
}

export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, isLoading, children, ...props }, ref) => (
    <button className={cn(buttonVariants({ variant, size, className }))} ref={ref} disabled={isLoading || props.disabled} {...props}>
      {isLoading ? <span className="spinner mr-2 animate-spin">⏳</span> : null}
      {children}
    </button>
  )
);
Button.displayName = "Button";
```

---

## Verification Checklist

- [ ] Components use `forwardRef` to pass DOM ref nodes cleanly.
- [ ] Component props support variant style generators (`cva`).
- [ ] All interactive components include explicit disabled, loading, and focus states.
