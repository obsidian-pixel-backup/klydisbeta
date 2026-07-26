---
name: web-analytics-privacy-compliance
description: Implementing privacy-first web telemetry: GDPR/CCPA consent banners, cookieless analytics, event tracking schemas, and data minimization.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Web Analytics & Privacy Compliance

Modern web telemetry balances user analytics collection with strict legal compliance (GDPR, CCPA, ePrivacy Directive).

## Core Privacy Principles

1. **Consent Prior to Tracking**: Non-essential tracking scripts must be blocked until explicit user consent is granted.
2. **Data Minimization**: Collect only metrics necessary for analytics without storing IP addresses or PII.
3. **Right to Erasure**: Provide mechanisms to delete stored telemetry associated with user identifiers.

---

## Privacy-First Telemetry Wrapper Blueprint

```typescript
type EventSchema = {
  eventName: string;
  properties?: Record<string, string | number | boolean>;
};

class PrivacyTelemetry {
  private hasConsent = false;

  setConsent(granted: boolean) {
    this.hasConsent = granted;
    if (granted) this.flushQueue();
  }

  track({ eventName, properties }: EventSchema) {
    if (!this.hasConsent) return; // Drop or queue until consent

    // Strip sensitive fields
    const sanitizedProps = { ...properties };
    delete sanitizedProps.email;
    delete sanitizedProps.ip;

    fetch('/api/telemetry', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ eventName, properties: sanitizedProps, ts: Date.now() })
    });
  }

  private flushQueue() { /* Send queued events */ }
}

export const telemetry = new PrivacyTelemetry();
```

---

## Verification Checklist

- [ ] Cookie consent banner blocks tracking scripts prior to user agreement.
- [ ] Telemetry events scrub personal identifiable information (PII) automatically.
- [ ] Analytics backend supports data deletion requests by user ID.
- [ ] Privacy policy documentation accurately discloses all telemetry integrations.
