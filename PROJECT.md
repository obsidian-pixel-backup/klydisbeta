# Project: Klydis Beta Reddit Developer Marketing & Showcase Campaign

## Architecture
- **Data Source**: `reddit_campaign_posts.md` containing subreddit-specific post titles, verbatim markdown bodies, community flairs, and GitHub repository links (`https://github.com/obsidian-pixel-backup/klydisbeta`).
- **Automation Engine**: Headful Chrome DevTools Protocol automation using Python 3.14 + `nodriver` (v0.50.3) controlling Google Chrome (v152.0.7977.65) with persistent profile at `.agents/reddit_browser_profile`.
- **UI Form Interaction**: Web component DOM manipulation (targeting title textareas, markdown switch toggles, markdown body inputs, flair modals, and post submit buttons).
- **Verification Engine**: Base36 regex permalink parser (`https://www.reddit.com/r/{sub}/comments/{id}`), HTTP GET/HEAD prober with custom User-Agent, and Reddit JSON API verifier.
- **Deliverable**: 7-section audit compliance report written to `reddit_campaign_report.md`.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | Post Content Extraction | Extract titles, markdown bodies, flairs, and links from `reddit_campaign_posts.md` for r/LocalLLaMA, r/dotnet, r/csharp, r/MachineLearning. | M1 | Survey (spec_miner) |
| 2 | Rule 8 Enforcement | Ensure mandatory `[P]` title prefix on `r/MachineLearning`. | M1 | Survey (spec_miner) |
| 3 | Visible CDP Automation | Headful Chrome browser launch with stealth CDP connection and persistent profile. | M1 | Survey (explorer_1) |
| 4 | Markdown Mode & DOM Input | Switch submit editor to Markdown mode and dispatch synthetic DOM input/change events for title, body, and flairs. | M2 | Survey (explorer_2) |
| 5 | Authentication & CAPTCHA Gating | Handle login redirect/CAPTCHA with interactive human pause; fallback to authentic staging state without synthetic mock URLs. | M2 | Survey (explorer_2) |
| 6 | Base36 Permalink Capture | Capture redirected post URLs matching canonical `comments/([a-z0-9]{5,8})` and extract `t3_<id>`. | M2 | Survey (explorer_2) |
| 7 | Live URL Verification Probe | Perform HTTP GET/HEAD verification on captured permalinks and check status code 200, server header, and Content-Type. | M3 | Survey (spec_miner) |
| 8 | 7-Section Campaign Report | Compile `reddit_campaign_report.md` adhering strictly to the 7-section audit schema with zero mock permalinks. | M3 | Survey (spec_miner) |
| 9 | Opaque-Box E2E Test Suite | Comprehensive multi-tier test suite verifying report structure, post fidelity, repo links, and zero-mock integrity. | M4 | Survey (top-level) |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | Engine & Post Parsing Verification | Validate publisher script, post parsers, `nodriver` launcher, and persistent profile setup | none | PLANNED |
| 2 | Headful Browser Automation & Post Submission | Execute visible browser automation across all 4 subreddits, input content/flairs, submit, and capture Base36 URLs | M1 | PLANNED |
| 3 | Live Verification & Campaign Report Compilation | Run live URL probes and generate the 7-section `reddit_campaign_report.md` | M2 | PLANNED |
| 4 | Final E2E Test Pass & Forensic Audit | Run full E2E test verification suite and complete forensic audit for integrity and zero mock URLs | M3 | PLANNED |

## Interface Contracts
### `reddit_campaign_posts.md` ↔ `reddit_publisher.py`
- Formats: Markdown headers `# Target Subreddit: r/...`, `## Post Title`, `## Post Body (Markdown)`, `## Recommended Flair`.
- Data types: UTF-8 strings, regex extractors.

### `reddit_publisher.py` ↔ Reddit Web UI
- Selectors: `textarea[name="title"]`, `faceplate-textarea-input[name="title"] textarea`, markdown switch button, `textarea[name="body"]`, `shreddit-post-flair-button button`, `shreddit-post-submit-button button`.
- Submission Redirect URL format: `^https://(?:www|sh|new)\.reddit\.com/r/[A-Za-z0-9_]+/comments/([a-z0-9]{5,8})(?:/.*)?$`

### `reddit_publisher.py` ↔ `reddit_campaign_report.md`
- Output Schema: 7 Sections (Executive Summary, Master Matrix, Positioning Rationale, Verbatim Posts, Codebase Truth Matrix, Live URL Evidence, Acceptance Criteria Compliance).
- Integrity Rule: Zero synthetic/mock URLs (`comments/klydis_...`). Staging state must reflect genuine session status.

## Code Layout
- `reddit_campaign_posts.md`: Source post content and metadata
- `reddit_campaign_report.md`: Final deliverable report
- `.agents/reddit_publisher.py`: Headful automation runner, probe, and report generator
- `.agents/reddit_browser_profile/`: Persistent Chrome user data directory
- `tests/`: Automated test suites
