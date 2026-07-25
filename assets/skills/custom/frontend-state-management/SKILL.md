---
name: frontend-state-management
description: Architecture standards for managing state in complex frontend applications — unidirectional data flow, immutability, state normalization, optimistic UI updates, local vs global state separation, and side-effect isolation. Use when designing frontend state stores (Redux, Zustand, Vuex, Pinia, WPF MVVM), building UI features, or refactoring frontend code.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Frontend State Management

Managing application state predictably is the core challenge of modern frontend engineering. Unstructured global state leads to UI sync bugs, hard-to-trace re-renders, and race conditions.

## State Classification

Divide state into four clear categories:

1. **Local Component State**: UI toggle state, dropdown open/close, form inputs. Kept inside the component.
2. **Global Application State**: User session, active theme, global notifications.
3. **Server Cache State**: Remote API data (managed by specialized tools like TanStack Query / RTK Query).
4. **URL / Route State**: Search filters, active page index, active tab ID. Must be stored in the URL so links are shareable.

## Principles of Predictable State

- **Unidirectional Data Flow**:
  `Action ➔ Reducer / Store ➔ State Update ➔ View Re-render`
- **Immutability**: Never mutate state objects directly. Always return new object references during updates:
  ```typescript
  // BAD: state.user.name = "Alice";
  // GOOD: return { ...state, user: { ...state.user, name: "Alice" } };
  ```
- **State Normalization**: Store entity collections as normalized maps (`byId`, `allIds`) rather than deeply nested arrays:
  ```json
  {
    "users": {
      "byId": {
        "user_1": { "id": "user_1", "name": "Alice" }
      },
      "allIds": ["user_1"]
    }
  }
  ```
- **Optimistic Updates**: Immediately update local UI state before the server HTTP request completes, with an automated rollback handler if the API returns an error.

## Checklist

- [ ] URL contains shareable view parameters (search, pagination, filters)
- [ ] Server data cached and invalidated via query hooks, not stored raw in global UI state
- [ ] State objects updated immutably
- [ ] Entity data normalized to avoid duplicate out-of-sync copies
