---
name: unit-integration-test-architecture
description: Architecting test suites: AAA pattern, test fixtures, test doubles (mocks, stubs, spies), property-based testing, code coverage, and integration testing.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Unit & Integration Test Architecture

A robust test architecture provides confidence that software behaves correctly across units, integration boundaries, and end-to-end flows.

## The Testing Pyramid

```
       /\
      /  \      E2E Tests (10%) - Slow, expensive, full browser / system
     /----\
    /      \    Integration Tests (30%) - DB, API routes, Repositories
   /--------\
  /          \  Unit Tests (60%) - Fast, isolated function & class logic
 /------------\
```

---

## Test Structuring: AAA Pattern (Arrange-Act-Assert)

Every test block should cleanly segregate the setup, execution, and verification phases:

```typescript
describe('UserService - registerUser', () => {
  it('should create user and hash password correctly', async () => {
    // 1. Arrange
    const mockRepo = { save: jest.fn().mockResolvedValue(true) };
    const service = new UserService(mockRepo);
    const inputDto = { email: 'user@example.com', password: 'Password123!' };

    // 2. Act
    const result = await service.registerUser(inputDto);

    // 3. Assert
    expect(result.email).toBe('user@example.com');
    expect(result.passwordHash).not.toBe('Password123!');
    expect(mockRepo.save).toHaveBeenCalledTimes(1);
  });
});
```

---

## Test Doubles Definitions

- **Dummy**: Values passed but never actually used.
- **Stub**: Provides canned answers to calls made during the test.
- **Spy**: Records calls, arguments, and return values for later assertion.
- **Mock**: Objects pre-programmed with expectations that form a specification.
- **Fake**: Working implementation with a shortcut (e.g., in-memory DB).

---

## Verification Checklist

- [ ] Unit tests run fast (<10ms per test) without external network/file I/O.
- [ ] Tests test public interface behaviors rather than private implementation details.
- [ ] Test names describe expected business behavior (`should_throw_error_when_email_invalid`).
- [ ] Code coverage metrics monitor key domain logic branches ($>80\%$ threshold).
