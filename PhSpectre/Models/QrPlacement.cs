namespace PhSpectre.Models;

// Where the QR block sits on the exported frame. Below is the only value in v1 — Overlay
// (photo bottom-left corner, translucent backing plate) was cut (v3 spec §1b): a real recipe
// payload at Medium correction runs version 6-7 (41-45 modules), and a grid that size read as
// visually heavy laid over the photo regardless of payload size. Can come back as its own task
// if there's real demand, likely restricted to compact presets (Camera-only, Custom URL) where
// the QR stays small. Kept as an enum (not collapsed to a bool/removed) so a future Overlay
// value slots back in without another round of plumbing changes.
//
// Whether a caption is drawn beside the QR, and what it says, is QrCaption — an independent
// axis (v4 spec §1b), not a Placement variant (v3's "BelowWithCaption" coupled the two and
// made "just QR" or "QR + camera only, no lens" impossible to express).
public enum QrPlacement { Below }
