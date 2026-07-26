---
name: ui-framer-motion-animation
description: Implementing Framer Motion animations in React: layout animations, gesture controls, page transition routes, stagger children, and Presence exit effects.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# Framer Motion Animation Architecture

Framer Motion provides declarative animation primitives for React components, facilitating page transitions, gesture interactions, and exit animations.

## Core Primitives

- `<motion.div>`: Animated HTML element wrapper.
- `AnimatePresence`: Enables exit animations when React components unmount.
- `layout`: Automatically animates layout changes smooth between DOM updates.

---

## Staggered Card Grid Blueprint

```typescript
import { motion, AnimatePresence } from 'framer-motion';

const containerVariants = {
  hidden: { opacity: 0 },
  visible: {
    opacity: 1,
    transition: { staggerChildren: 0.1 }
  }
};

const cardVariants = {
  hidden: { opacity: 0, y: 20 },
  visible: { opacity: 1, y: 0, transition: { duration: 0.4 } },
  exit: { opacity: 0, scale: 0.95 }
};

export function CardGrid({ items }: { items: Array<{ id: string; title: string }> }) {
  return (
    <motion.div
      variants={containerVariants}
      initial="hidden"
      animate="visible"
      className="grid grid-cols-3 gap-4"
    >
      <AnimatePresence>
        {items.map((item) => (
          <motion.div
            key={item.id}
            variants={cardVariants}
            exit="exit"
            layout
            className="p-4 bg-slate-800 rounded-xl"
          >
            <h3>{item.title}</h3>
          </motion.div>
        ))}
      </AnimatePresence>
    </motion.div>
  );
}
```

---

## Verification Checklist

- [ ] `AnimatePresence` wraps conditional component branches that feature `exit` props.
- [ ] Animated elements use `layout` prop for automatic layout reflow transitions.
- [ ] Gesture animations (`whileHover={{ scale: 1.02 }}`) use subtle scale offsets.
