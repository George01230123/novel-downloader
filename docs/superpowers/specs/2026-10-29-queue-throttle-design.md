# Queue Throttle Design

**Intent:** Keep multi-book downloads sequential and retain the normal per-book worker count, while carrying site rate-limit feedback across books in one queue so the next book does not immediately restart at full concurrency.

**Approaches:** Parallel books would increase aggregate request pressure and conflict with the rate-limit requirement. A fixed pause between every book would slow healthy runs without evidence that a pause is needed. The selected approach, requested by the user, runs books sequentially and carries adaptive throttle state between them.

**Constraints:** Standalone downloads continue to reset throttle state at start. Each new queue run resets once; books within that run share adaptive throttle state. Existing chapter ordering, retry behavior, and per-site worker settings remain unchanged. Rate limits are site-controlled, so this reduces repeated pressure after a limit is observed but cannot guarantee a site will never throttle.

**Implementation:** Add an opt-out for `DownloadRunner`'s start-of-run throttle reset, enable it only for queue jobs, and reset throttle state once when `RunQueue` begins. Add offline tests for both standalone reset behavior and queue preservation.
