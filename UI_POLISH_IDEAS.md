# UI Polish Ideas — SCADA Dashboard (Kişi 3)

Concrete, prioritized ways to make the app look nicer. Grounded in the current
screens: map window ([MapWindow.xaml](ui/ScadaDashboard/MapWindow.xaml)),
machine panel ([MainWindow.xaml](ui/ScadaDashboard/MainWindow.xaml)), the map
control ([PipelineMapControl.cs](ui/ScadaDashboard/Pipeline/PipelineMapControl.cs)),
settings, and the shared styles in [App.xaml](ui/ScadaDashboard/App.xaml).

Tags: **[QW]** quick win (minutes), **[M]** medium (an hour or two), **[B]** bigger effort.

---

## 0. Consistency cleanups (do these first — cheap, high payoff)

- **[QW] Unify corner radius.** Machine cards use `CornerRadius="19"`, the status
  badge inside them uses `CornerRadius="0"` (sharp), the settings/detail boxes use
  `6`. Pick a scale (e.g. 6 small / 10 medium / 16 card) and apply everywhere. The
  sharp status badge on a rounded card is the most visible mismatch.
- **[QW] One accent color.** `#F1C40F` (yellow) is used for the "Veri kaynağı" label
  but yellow also means "Uyarı/degraded health". Reusing a semantic color as a neutral
  accent is confusing. Give the app a single brand accent (a calm teal/blue) and keep
  yellow/orange/red strictly for health states.
- **[QW] Reuse brushes.** Several hardcoded hex values (`#0F1620`, `#E74C3C`,
  `#2ECC71`) are repeated inline across XAML and C#. Promote them all to `App.xaml`
  resources (`BgBrush`, `CriticalBrush`, `HealthyBrush`…) so a future re-theme is a
  one-file change.

---

## 1. Typography

- **[QW] Tabular figures for telemetry.** Numbers like `62.80 bar`, `%86`, `RUL 111`
  jitter horizontally as they update because Segoe UI uses proportional digits. Add
  `<Typography.NumeralAlignment>Tabular</Typography.NumeralAlignment>` (or the
  `tnum` feature) to value TextBlocks so digits stay column-aligned and stop twitching.
- **[QW] Stronger hierarchy.** Section labels ("SAĞLIK DURUMU", "KALAN ÖMÜR") are all
  the same muted 10–11px. Add letter-spacing (`FontStretch`/`CharacterSpacing` via a
  style) to the all-caps micro-labels — small caps with tracking reads as intentional
  and premium.
- **[M] A display font for the big header.** The "BORU HATTI AĞI" title is the same
  Segoe UI as body text. A slightly heavier or condensed face (still system-safe, e.g.
  Segoe UI Semibold/Black) for the H1 gives the header presence.

---

## 2. Color & theming

- **[M] Light theme option.** The app is dark-only. A light variant (swap the ~8
  brush resources) doubles perceived polish and helps in bright control rooms. Wire it
  to the existing settings window.
- **[QW] Softer background layering.** Right now bg (`#0F1620`) and land (`#182432`)
  are close in value. A subtle vertical gradient on the window background (very dark at
  top → slightly lighter at bottom) adds depth without noise.
- **[QW] Desaturate the map base.** The province fill/stroke could drop a few % of
  saturation so the colored nodes/flows pop more against a truly neutral map.

---

## 3. The map (biggest visual surface)

- **[M] Selection highlight.** Clicking a station opens the detail panel but the node
  itself gets no persistent emphasis. Draw a bright ring / outer glow on the selected
  node so the eye connects panel ↔ map.
- **[M] Smooth hover feedback.** Nodes currently pop between states. Animate a small
  scale-up + glow on hover (a 120ms ease) — the single biggest "feels alive" upgrade.
- **[M] Directional flow arrows.** The animated dots show flow *speed* but not
  *direction* clearly. Small chevrons riding the polyline (or teardrop-shaped dots
  pointing downstream) read direction at a glance.
- **[M] Label collision handling.** Still the open issue behind the earlier overlap
  complaint. Options: draw all nodes first then all labels last (labels never covered);
  skip a label when another node sits within ~25px; or fade labels in only above a zoom
  threshold. The category toggles help but don't solve dense clusters.
- **[QW] Glow bloom on critical nodes.** The pulsing red ring is good; add a soft
  radial glow behind critical stations (large, very transparent red ellipse) for a
  "hotspot" feel that draws attention from across the map.
- **[QW] Rounded line caps + subtle shadow on pipelines.** Confirm `StrokeLineJoin`
  is Round at polyline vertices so bends look smooth; a faint dark under-stroke gives
  segments a lifted look over the map.
- **[B] Zoom-to-region on click.** Double-click a station to ease-zoom into its
  cluster. Pairs naturally with the node-scaling idea and makes dense areas usable.

---

## 4. Machine cards (MainWindow)

- **[QW] Elevation / drop shadow.** Cards are flat borders on a flat panel. A soft
  `DropShadowEffect` (large blur, low opacity, slight Y offset) separates cards from
  the background and reads instantly as "modern".
- **[M] Hover lift.** On mouse-over, nudge the card up 2px + deepen the shadow (short
  animation). Cheap, delightful.
- **[QW] Health bar gradient + rounded ends.** The `HealthBar` ProgressBar is a solid
  fill. A subtle gradient (darker→brighter of the same status hue) and fully rounded
  caps look more finished. A thin track behind it at low opacity helps too.
- **[QW] Status badge as a pill.** Round the status badge (see §0) and give it a faint
  tinted background of its own color at ~15% opacity with the solid color as text —
  softer than the current solid block.
- **[M] Sensor row icons.** Prefix "Sıcaklık / Basınç / Titreşim" with small glyphs
  (thermometer, gauge, wave) from Segoe Fluent Icons — faster scanning, more visual
  texture.

---

## 5. Charts (MiniChart)

- **[M] Area fill under the line.** A vertical gradient from the line color (top,
  ~25% alpha) to transparent (bottom) under each sparkline turns thin lines into
  richer, dashboard-grade charts.
- **[QW] Current-value dot.** Draw a filled dot at the latest point of each mini-chart
  (the machine panel already implies this — make it consistent on the detail panel
  sparklines too) with a faint halo.
- **[QW] Faint baseline / threshold line.** A dashed horizontal line at the
  warning/critical threshold gives the squiggle meaning at a glance.

---

## 6. Detail panel (21-sensor list)

- **[QW] Zebra striping or hairline separators.** 21 rows of `name … value` is a lot
  of undifferentiated text. Alternate row background at ~4% opacity, or thin separators,
  makes it scannable.
- **[QW] Right-align + monospace the values.** Values already right-align; add tabular
  figures (see §1) and a fixed unit column so `bar`, `°C`, `mm/s` line up vertically.
- **[M] Group the sensors.** Cluster the 21 into "Basınç / Sıcaklık / Titreşim /
  Performans" subheaders instead of one flat list — turns a data dump into a readable
  spec sheet.
- **[QW] Color the out-of-range values.** If a sensor exceeds a nominal band, tint just
  that value red/amber. Instant anomaly spotting.

---

## 7. Window chrome & micro-interactions

- **[M] Windows 11 rounded corners + dark title bar.** Opt into the DWM dark title bar
  and rounded window corners (a few P/Invoke calls at window init) so the app matches
  Win11 instead of showing a light default title bar over a dark UI.
- **[M] Fade-in on window open + panel slide.** The detail panel appears instantly;
  slide it in from the right (150–200ms ease-out). Fade the whole window in on launch.
- **[QW] Button hover transitions.** `GhostButton` swaps background instantly on hover.
  A 100ms color transition (Storyboard on `IsMouseOver`) feels smoother.
- **[M] Value-change flash.** When a telemetry number crosses into a worse state,
  briefly flash its background — draws the eye to what changed.

---

## 8. Icons & assets

- **[QW] Icon font for buttons.** Replace text-only buttons ("Makine Paneli",
  "Ayarlar", the "X" close) with Segoe Fluent Icons glyph + label. The bare "X" close
  on the detail panel especially looks unfinished.
- **[QW] Empty/placeholder states.** If the snapshot is missing or a station has no
  units, show a small centered icon + message instead of a blank panel.

---

## 9. Accessibility (also just looks better)

- **[QW] Contrast check the muted text.** `#8CA3B8` on `#0F1620` is borderline for
  11px labels. Nudge it lighter for the smallest text.
- **[M] Don't rely on color alone for health.** Add a tiny shape/glyph difference
  (or the status word) so red/green colorblind users can still read station state on
  the map, not just in the panel.

---

## Suggested order (by payoff-to-effort)

1. §0 consistency cleanups + §1 tabular figures — makes everything feel intentional.
2. §4 card shadows + hover lift — biggest "modern" jump for the machine panel.
3. §3 map selection highlight + hover animation — biggest jump for the map.
4. §7 Win11 chrome + panel slide-in — polish the frame.
5. §5/§6 chart area-fill + sensor grouping — data readability.
6. §2 light theme — nice-to-have, larger scope.

---

*Design ideas only — no code changed. Local working doc; add to `.gitignore` if you
want it kept out of the repo like AI_HANDOFF.md / DECISIONS.md.*
