---
name: form-architecture-validation
description: Architecting complex forms: Zod/Yup schema validation, React Hook Form, multi-step wizard state, optimistic updates, and accessible error handling.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Form Architecture & Schema Validation

Form handling requires seamless client-side validation, accessible keyboard focus management, state preservation across steps, and robust error messages.

## Recommended Stack: React Hook Form + Zod

- **Zod**: Type-safe schema validation engine with automatic TypeScript type inference.
- **React Hook Form**: Uncontrolled input form state engine preventing unnecessary component re-renders.

---

## Form Implementation Blueprint

```typescript
import React from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';

const registerSchema = z.object({
  email: z.string().email('Invalid email address'),
  password: z.string().min(8, 'Password must be at least 8 characters'),
  age: z.number().min(18, 'Must be at least 18 years old')
});

type RegisterFormData = z.infer<typeof registerSchema>;

export function RegistrationForm() {
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting }
  } = useForm<RegisterFormData>({
    resolver: zodResolver(registerSchema)
  });

  const onSubmit = async (data: RegisterFormData) => {
    await fetch('/api/register', { method: 'POST', body: JSON.stringify(data) });
  };

  return (
    <form onSubmit={handleSubmit(onSubmit)} className="space-y-4">
      <div>
        <label htmlFor="email">Email</label>
        <input id="email" {...register('email')} aria-invalid={!!errors.email} />
        {errors.email && <span className="error">{errors.email.message}</span>}
      </div>

      <button type="submit" disabled={isSubmitting}>
        {isSubmitting ? 'Submitting...' : 'Register'}
      </button>
    </form>
  );
}
```

---

## Verification Checklist

- [ ] Form schemas validate inputs client-side and re-use schema server-side.
- [ ] Input fields feature `aria-invalid` and `aria-describedby` error bindings.
- [ ] Submit buttons display clear loading state and disable double-clicks.
- [ ] Server validation errors populate back into corresponding form fields.
