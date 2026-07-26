---
name: ux-interaction-design-patterns
description: Designing interaction UX patterns: optimistic UI updates, undo/redo mechanisms, drag-and-drop UX, infinite scroll vs pagination, and keybindings.
category: UX & User Experience
author: Klydis Team
version: 2.0.0
---

# UX Interaction Design Patterns

Interaction design defines how software responds dynamically to user inputs, creating responsive, friction-free experiences.

## Essential Interaction Patterns

1. **Optimistic UI Updates**: Immediately update the client UI before server confirmation, rolling back gracefully only if the network request fails.
2. **Undoable Actions (Toast Snackbars)**: Allow users to instantly undo destructive actions (e.g., deleting an item) via an "Undo" prompt rather than blocking workflow with modal dialogs.
3. **Infinite Scroll vs Pagination**: Use infinite scroll for discovery feeds (social, images); use explicit pagination for search results and tables where items need direct URL bookmarking.

---

## Optimistic UI Pattern Blueprint (TypeScript / React Query)

```typescript
import { useMutation, useQueryClient } from '@tanstack/react-query';

export function useOptimisticLike() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (postId: string) => fetch(`/api/posts/${postId}/like`, { method: 'POST' }),
    onMutate: async (postId) => {
      await queryClient.cancelQueries({ queryKey: ['post', postId] });
      const previousPost = queryClient.getQueryData(['post', postId]);

      // Optimistically increment like count in UI immediately
      queryClient.setQueryData(['post', postId], (old: any) => ({
        ...old,
        likes: old.likes + 1
      }));

      return { previousPost };
    },
    onError: (err, postId, context) => {
      // Rollback UI to previous state if API call fails
      queryClient.setQueryData(['post', postId], context?.previousPost);
    }
  });
}
```

---

## Verification Checklist

- [ ] Optimistic updates include rollback error handlers in case of server failure.
- [ ] Destructive actions provide an instant "Undo" toast prompt.
- [ ] Keyboard shortcuts (e.g., `Cmd+K` for search palette) do not conflict with browser default keys.
