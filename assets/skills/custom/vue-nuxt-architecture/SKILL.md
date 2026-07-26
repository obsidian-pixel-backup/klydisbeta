---
name: vue-nuxt-architecture
description: Architecting Vue 3 and Nuxt applications: Composition API, script setup, Pinia state management, SSR/SSG rendering modes, and composables.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Vue & Nuxt Architecture

Vue 3 with Nuxt 3 provides a powerful framework for building full-stack web applications with reactive Composition API, auto-imports, and flexible SSR/SSG rendering.

## Composition API & `<script setup>` Standard

Prefer `<script setup>` syntax for clean, concise component logic:

```vue
<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';

interface UserProps {
  initialCount?: number;
}

const props = withDefaults(defineProps<UserProps>(), {
  initialCount: 0
});

const emit = defineEmits<{
  (e: 'update', count: number): void;
}>();

const count = ref(props.initialCount);
const doubleCount = computed(() => count.value * 2);

function increment() {
  count.value++;
  emit('update', count.value);
}
</script>

<template>
  <div class="counter-card">
    <p>Count: {{ count }} (Double: {{ doubleCount }})</p>
    <button @click="increment">Increment</button>
  </div>
</template>
```

---

## Pinia Store Architecture Blueprint

```typescript
// stores/userStore.ts
import { defineStore } from 'pinia';
import { ref, computed } from 'vue';

export const useUserStore = defineStore('user', () => {
  const user = ref<{ id: string; name: string } | null>(null);
  const isAuthenticated = computed(() => user.value !== null);

  async function fetchUser() {
    const data = await $fetch('/api/user');
    user.value = data;
  }

  return { user, isAuthenticated, fetchUser };
});
```

---

## Verification Checklist

- [ ] Components use `<script setup lang="ts">` exclusively.
- [ ] Reusable reactive logic is extracted into composables (`composables/useAuth.ts`).
- [ ] State management uses Pinia instead of legacy Vuex.
- [ ] Nuxt pages implement proper SEO meta wrappers (`useSeoMeta`).
