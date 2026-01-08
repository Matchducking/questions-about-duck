# Match Ducking

Match Ducking is a coordinated exploitation and automation project targeting long-standing, unpatched vulnerabilities in the **Among Us** matchmaking, moderation, and server infrastructure.

The purpose of these actions is not entertainment or griefing, but to **force InnerSloth to acknowledge and patch exploits** that have been repeatedly reported through conventional channels and ignored for months.

This repository documents:

* What Match Ducking is
* Why it exists
* What actions we take responsibility for
* What technical evidence supports those claims
* Which projects, techniques, and external works were involved

Q&A and discussion take place in **GitHub Discussions**.

---

## Table of Contents

* Overview
* Terminology
* Mission & Rationale
* Disclosure History
* Scope of Responsibility
* Operational Phases & Timeline
* Projects & Samples
* Proof of Identity
* Technique Disclosure Policy
* Credits & Influences
* Relationship With Other Projects
* InnerSloth Incidents & Public Messages
* Current Status
* Future Intent
* Community

---

## Overview

Match Ducking refers to the deliberate, automated exploitation of server-side vulnerabilities in Among Us in order to cause large-scale disruption of public matchmaking.

Unlike typical cheat menus or casual griefing:

* Actions are automated at scale
* Clients are purpose-built rather than repurposed menus
* Operations are targeted and time-bound
* Escalation is reduced once developer awareness is confirmed

The objective is to **force developer response**, not to permanently ruin the player experience.

---

## Terminology

**Ducking**
In this context, “ducking” refers specifically to exploiting server or matchmaking vulnerabilities (kick, ban, lag, overload, matchmaking abuse) to make games unplayable at scale.

This term is used to distinguish the activity from casual cheating or standard modding.

---

## Mission & Rationale

Multiple severe exploits in Among Us have existed for months and were reported through:

* Official support channels
* Developer contact forms
* Community moderation pathways

These reports consistently resulted in **automated responses** or no meaningful follow-up.

> Evidence is documented at:
> [Innersloth Contact Attempts](https://github.com/Matchducking/innersloth-contact-attempts)

Based on repeated observation, InnerSloth only meaningfully reacts when:

* Exploits are publicly visible at scale
* Large volumes of player reports overwhelm support systems
* Matchmaking becomes visibly unusable

Match Ducking was chosen as an escalation mechanism after normal disclosure failed.

---

## Disclosure History

We did not begin with aggressive actions.

Exploits were:

* Reported months in advance
* Demonstrated with reproducible steps
* Ignored or answered with generic bot responses

During this time, InnerSloth continued prioritizing:

* Cosmetic releases
* Monetization
* Minimal moderation tooling

This pattern left escalation as the only remaining lever.

---

## Scope of Responsibility

We take responsibility for the following operations:

* **January 2025**
  * Large-scale bot ban wave (MatchHacking project)

* **April 2025**

  * Vote-kick abuse raids
  * DuckDater fake dating room hosting
  * Matchmaking pressure via targeted lobby configurations

* **December 2025-January 2026**

  * Vote-kick abuse raids
  * GameData Overload
  * Specifically North America

We **do not** take responsibility for:

* Early lobby-murder bots
* Parasites Central spam bots
* Other unrelated raid waves

Those were conducted by separate actors with possibly malicious intent.

---

## Operational Phases & Timeline

### Phase 1 — Aggressive Escalation

* Mass lagging
* Forced bans and kicks
* Making games temporarily unplayable

Purpose:
Confirm developer awareness and internal escalation.

### Phase 2 — Reduced Aggression

Once InnerSloth awareness was confirmed:

* Bots stopped crashing players
* Presence was maintained without full disruption
* Focus shifted to signaling urgency

### Dating Lobby Targeting

During April raids, it appeared that InnerSloth “patched” kick bots quickly.
This was misleading.

We learned, via DuckoMenu’s Discord, that **dating lobbies** remained a persistent moderation failure:

* 4–6 player rooms
* 1 impostor
* Skeld map
* Used by predatory or abusive communities

InnerSloth showed no willingness to address this.

Response:

* Kick bots were halted
* A new **duck hoster** was built
* Fake dating rooms were hosted at scale

This approach:

* Reduced harm to normal players
* Targeted the neglected problem directly
* Maintained pressure without mass disruption

---

## Projects & Samples

### Host Ducks (Released October 8)

Capabilities:

* Hosting thousands of lobbies
* Matchmaking overload
* Targeted lobby configuration spoofing

Primary target:

* Dating lobby configurations
* Exploitation of matchmaking gaps

This project represents the technical foundation of MatchDucking.

---

## Proof of Identity

To establish authorship and responsibility, we have included **partial source code** for key components of our duck bots.

This code is:

* Sufficient to prove identity
* Insufficient to allow trivial replication

---

## Technique Disclosure Policy

Most techniques used are referenced indirectly in the Credits section and are believed to be patched.

We do **not** disclose:

* Still-unpatched exploits
* Techniques that are excessively destructive
* Methods likely to be abused by unrelated actors

This is intentional.

---

## Credits & Influences

We independently coded our tooling, but referenced publicly available work.

Credit does **not** imply endorsement or involvement.

### GitHub Copilot

Used to reduce development time and coding overhead.

---

### SickoMenu

A technically mixed project.

Relevant contributions:

1. Friendcode spacing to bypass guest matchmaking limits
2. ProtectPlayer, QuickChat, and overload exploits
3. Exposure of dating lobby issues

---

### MalumMenu

1. Guest account generation via friendcodes
2. Inspiration for vote-kick exploitation

> Vote kick remains unpatched in the latest anticheat.

---

### EzHacked

Original source of:

* cmd checkname
* cmd check color
* cmd-based ban exploits

These were later reused by SickoMenu and subsequently by us during January raids.

---

### Impostor

Used for:

1. Local testing of duck clients
2. JoinGame and HostGame packet references
3. FilterGames exploit research

> In PR #685, NikoCat233 revealed a filter exploit allowing cross-language, cross-chat room discovery.

---

### NikoCat233’s Impostor Server

NikoCat233 and Pietro built a full HTTP API for:

* MM token requests
* Friendcode operations

These branches are now private, preventing proper credits.

---

### among-us-protocol

[https://github.com/roobscoob/among-us-protocol](https://github.com/roobscoob/among-us-protocol)
Outdated, but useful as a reference wiki.

---

### Reactor

Provided the game assembly used to derive protocol behavior.

---

## Relationship With SickoMenu

Some believe SickoMenu developers are behind Match Ducking.

This is false.

We:

* Used some of their techniques
* Are not affiliated with them
* Posted joke messages referencing them

These jokes were discontinued once confusion arose.
We did not anticipate users downloading cheats to fight ducks.

---

## InnerSloth Incidents & Public Messages

We acknowledge irresponsible jokes made during operations, including references to SuperSus.

Examples include:

```
MatchDucking : Global Offensive
Powered by SuperSus dot io
```

Other injected messages highlighted:

* Friendcode spacing abuse
* Moderation failures
* Dating lobby negligence

---

## Current Status

InnerSloth has patched several exploits that were previously ignored.

Active ducking operations are currently inactive.

---

## Future Intent

If new severe exploits emerge and:

* Are responsibly disclosed
* Are ignored for extended periods
* Cause systemic harm to matchmaking

Then escalation may occur again.

This repository will remain as documentation.

---

## Community

* GitHub Discussions for Q&A
* Discord: [https://discord.gg/qq84Dq6dH2](https://discord.gg/qq84Dq6dH2)

> Good Night Victoria Support Team and Sloth Dev
![Victoria Support](images/victoria.png)
