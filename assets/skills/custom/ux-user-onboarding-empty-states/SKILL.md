---
name: ux-user-onboarding-empty-states
description: Designing user onboarding: First-Time User Experience (FTUE), interactive product walkthroughs, informative empty states, and skeleton loading.
category: UX & User Experience
author: Klydis Team
version: 2.0.0
---

# UX User Onboarding & Empty State Architecture

Onboarding guides new users through initial application setup, while informative empty states turn blank views into actionable learning opportunities.

## Empty State Anatomy

Never present a completely blank screen when a list or table contains zero items. Include:
1. **Friendly Visual Asset**: Illustration or icon representing the domain concept.
2. **Clear Explanation Title**: Explain why the screen is currently empty ("No active projects found").
3. **Actionable Call-to-Action**: Provide a button allowing the user to create their first item immediately.

---

## Production Empty State Component Blueprint

```html
<div class="empty-state-card text-center p-12 bg-slate-900/50 border border-dashed border-slate-800 rounded-2xl max-w-md mx-auto">
  <!-- Visual Illustration -->
  <div class="w-16 h-16 mx-auto mb-4 bg-blue-500/10 text-blue-400 rounded-full flex items-center justify-center text-2xl">
    📁
  </div>

  <!-- Explanatory Header -->
  <h3 class="text-lg font-semibold text-white mb-2">No projects created yet</h3>
  <p class="text-sm text-slate-400 mb-6">
    Get started by creating your first workspace project to begin collaborating.
  </p>

  <!-- Primary Action -->
  <button class="btn-primary">
    + Create First Project
  </button>
</div>
```

---

## Verification Checklist

- [ ] Views with zero data render an informative empty state card with a CTA.
- [ ] First-time users see a guided onboarding tour highlighting key actions.
- [ ] Onboarding tours allow users to skip or dismiss the walkthrough anytime.
