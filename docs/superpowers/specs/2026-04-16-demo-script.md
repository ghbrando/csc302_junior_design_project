# UniCore Demo Script
**Date:** April 2026  
**Duration:** 5 minutes (~20 blocks × 15 seconds)  
**Format:** Consumer-led narrative. Consumer app drives the story; providers react visibly.  
**Setup:** 1 laptop — 1 Consumer app, 2 Provider apps (Provider A = light mode, Provider B = dark mode)

---

## Prerequisites Checklist (Before Demo)

- [ ] All three apps running: `providerunicore` ×2, `consumerunicore`
- [ ] Provider A in **light mode**, Provider B in **dark mode**
- [ ] VM named `my-portfolio-vm` already provisioned on Provider A (web files in place, server NOT yet running)
- [ ] Firestore `virtual_machines` collection cleared of stale entries
- [ ] Consumer logged in and on Dashboard
- [ ] Both Providers logged in and on Dashboard
- [ ] VM names use `-` only — no spaces or special characters
- [ ] Presenter is on Hotspot or VPN

---

## Presenter Assignments

| Presenter | Role |
|-----------|------|
| A | Consumer app — primary driver throughout |
| B | Provider A (light mode) — reacts, shows revenue |
| C | Provider B (dark mode) + VM lifecycle wrap-up |

---

## Script

### Segment 1 — Introduction (0:00–0:30)

**Block 1 — 0:00–0:15 | Presenter A (narrates), B + C visible**
> "UniCore is a peer-to-peer VM marketplace. Providers rent out spare hardware — you can see two providers running here, one in light mode and one in dark. Consumers rent Linux VMs through the browser, no setup required."

- Show both Provider dashboards briefly, then switch focus to Consumer app.

**Block 2 — 0:15–0:30 | Presenter A (Consumer Dashboard)**
> "This is the Consumer dashboard. Right now it's empty — let's launch a VM."

- Consumer Dashboard is visible and clean.

---

### Segment 2 — Launching a VM (0:30–1:00)

**Block 3 — 0:30–0:45 | Presenter A**
> "We'll click 'New VM', give it the name `demo-launch-vm`, and select our specs."

- Open Create VM modal.
- Enter name: `demo-launch-vm`
- Select CPU/RAM tier.

**Block 4 — 0:45–1:00 | Presenter A → Presenter B**
> "We submit the request — and watch what happens on the Provider side."

- Consumer submits. VM enters provisioning state.
- **Switch to Provider A** (Presenter B): VM card appears on Provider A's dashboard.
> "The provider instantly sees the new VM assigned to them."

---

### Segment 3 — Revenue Page (1:00–1:30)

**Block 5 — 1:00–1:15 | Presenter B (Provider A)**
> "Providers can track their earnings in real time. Let's jump to the Revenue page."

- Navigate to Revenue page on Provider A.
- Show balance card and revenue chart.

**Block 6 — 1:15–1:30 | Presenter B**
> "Every VM rental contributes to their balance. Providers can see payout history and request withdrawals."

- Point out payout history table and balance card.
- Hand focus back to Consumer.

---

### Segment 4 — Accessing the VM (1:30–2:00)

**Block 7 — 1:30–1:45 | Presenter A (Consumer Dashboard)**
> "Back on the consumer side — we'll switch to `my-portfolio-vm`, which was already provisioned before the demo."

- Click into `my-portfolio-vm` on the Consumer Dashboard.
- Open WebShell.

**Block 8 — 1:45–2:00 | Presenter A (WebShell)**
> "We now have a full Linux terminal running directly in the browser. This is a real shell on a real VM."

- Run a quick command (e.g., `ls` or `whoami`) to demonstrate it's live.

---

### Segment 5 — Public Hosting (2:00–2:45)

**Block 9 — 2:00–2:15 | Presenter A (browser)**
> "Each VM gets a public URL. Let's open it — right now nothing is running."

- Open the VM's public URL in the browser.
- Show the 404 / connection refused error.
> "Nothing there yet. Let's fix that."

**Block 10 — 2:15–2:30 | Presenter A (WebShell)**
> "Back in the terminal, we start a web server on port 8080."

- Type and run: `python3 -m http.server 8080`
- Server starts, output is visible in shell.

**Block 11 — 2:30–2:45 | Presenter A (browser)**
> "Now we refresh the public URL…"

- Refresh the browser tab.
- Portfolio website loads live.
> "The VM is now publicly hosting a portfolio site — reachable from anywhere."

---

### Segment 6 — Manual Backup (2:45–3:00)

**Block 12 — 2:45–3:00 | Presenter A (Consumer Dashboard)**
> "Before we migrate, let's take a manual snapshot to preserve the current state."

- On `my-portfolio-vm` card, open the kebab (⋮) menu.
- Click snapshot / backup option.
- Show confirmation toast or status update.

---

### Segment 7 — Migrating the VM (3:00–4:00)

**Block 13 — 3:00–3:15 | Presenter A (Consumer Dashboard)**
> "Now let's migrate this VM to a different provider — live, with no data loss."

- Open the kebab menu on `my-portfolio-vm`.
- Click Migrate.
- Migration dialog opens.

**Block 14 — 3:15–3:30 | Presenter A (Migration Dialog)**
> "During migration, the consumer can request a resource upgrade. We'll bump the CPU and RAM."

- Increase CPU cores (e.g., 1 → 2).
- Increase RAM (e.g., 1 GB → 2 GB).

**Block 15 — 3:30–3:45 | Presenter A (Migration Dialog)**
> "We'll target Provider B specifically — visible here in dark mode — and confirm."

- Select Provider B from the provider list.
- Click Confirm Migration.

**Block 16 — 3:45–4:00 | Presenter A + Presenter C**
> "Migration is in progress — home directory preserved, packages restored from snapshot."

- Consumer shows migration spinner + live status.
- **Switch to Provider C** (Presenter C): VM card appears on Provider B's dark-mode dashboard.
> "The VM has arrived on Provider B — upgraded specs, same data."

---

### Segment 8 — VM Lifecycle: Pause, Stop, Delete (4:00–4:45)

**Block 17 — 4:00–4:15 | Presenter A (Consumer Dashboard)**
> "Consumers have full lifecycle control. Let's pause `demo-launch-vm` — it freezes state without deleting it."

- Open kebab menu on `demo-launch-vm`.
- Click Pause.

**Block 18 — 4:15–4:30 | Presenter A**
> "Now stop it — this deallocates the VM but keeps the record."

- Click Stop from kebab menu.
- VM moves to stopped state / stopped table.

**Block 19 — 4:30–4:45 | Presenter A + Presenter C**
> "And finally, delete both VMs — cleaning up completely."

- Delete `demo-launch-vm` from Consumer.
- Delete `my-portfolio-vm` from Consumer.
- Presenter C: Both provider dashboards now show empty VM lists.

---

### Segment 9 — Wrap (4:45–5:00)

**Block 20 — 4:45–5:00 | Presenter A (narrates)**
> "That's UniCore — launch a VM, access it from the browser, host publicly, migrate with upgrades, and manage the full lifecycle. Providers earn revenue for every minute their hardware is rented."

- Show all three apps side by side (both providers clean, consumer dashboard empty).

---

## Timing Summary

| Time | Segment |
|------|---------|
| 0:00–0:30 | Introduction |
| 0:30–1:00 | Launching a VM |
| 1:00–1:30 | Revenue Page |
| 1:30–2:00 | Accessing the VM |
| 2:00–2:45 | Public Hosting (404 → start server → live site) |
| 2:45–3:00 | Manual Backup |
| 3:00–4:00 | Migration + Upgrade |
| 4:00–4:45 | Pause → Stop → Delete |
| 4:45–5:00 | Wrap |
