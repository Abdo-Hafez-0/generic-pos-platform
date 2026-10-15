# Matjar - brand sheet

Chosen by the owner on 2026-10-15 (MISS-09). The product was developed as "GenericPOS"; Matjar is its name from now on.

## Name

| | |
|---|---|
| English | **Matjar** (the logo writes it in spaced capitals: MATJAR) |
| Arabic | **متجر** ("store") |
| Feeling | premium, calm, trustworthy - for any small business, in Arabic and English markets alike |

"Matjar" is a common Arabic word. Before the installer is published (PKG-03), check that the name is free to use as a product
name in the countries of sale (trademark search); a short tag ("Matjar POS") or a distinctive spelling may be needed.

## Logo

The framed **م** with a sand-gold dot beneath it, followed by the name in both scripts: **متجر** above, **MATJAR** below.

| File | Use |
|---|---|
| `matjar-logo.svg` | light backgrounds (emerald on white) |
| `matjar-logo-dark.svg` | dark backgrounds (light text, warmer sand dot) |
| `matjar-icon.svg` | the application icon as a drawing (emerald tile, white frame and م, sand dot) |
| `matjar.ico` | the Windows icon: 16, 20, 24, 32, 40, 48, 64, 128 and 256 px, each drawn for its size (no inner frame below 32 px) |
| `matjar-icon-256.png`, `matjar-icon-48.png` | the icon as pictures |

Rules:
- The dot is always sand-gold, the only gold on screen near the logo; never add a second accent to the logo.
- Keep clear space around the logo of at least the height of the frame's corner radius; never stretch, rotate, outline or shadow it.
- Minimum size: the full logo 120 px wide; below that use the icon (the framed م).
- The texts in the SVG files use Windows fonts (Segoe UI for Arabic, Georgia for MATJAR). For print, signage or the website,
  have a designer convert them to outlines (and, ideally, refine the Arabic lettering).

## Colours

Palette 2, "Emerald and sand". Emerald is the brand and the working colour; sand is a rare highlight.

| Role | Light theme | Dark theme | Notes |
|---|---|---|---|
| Brand (logo, headings) | Emerald `#0F5132` | `#E9EEEA` | |
| Highlight (logo dot, open-menu marker) | Sand `#C8A96A` | `#D6B97E` | decoration only - never text on white (contrast 2.25) |
| Action (main buttons, links, selection) | `#0F7A4F`, white text | `#4CC38A`, **dark text** `#0F1612` | white on the dark action green is unreadable (2.22) |
| Checkout | `#0F7A4F`, white text | `#1E7B4F`, white text | |
| Page | `#FFFFFF` | `#0F1612` | |
| Surface (side menu, table headers, status bar) | `#F7F6F2` | `#141D18` | |
| Lines | `#E7E3D9` | `#283229` | |
| Selected row / menu item | `#E6F1EB` | `#1B2E24` | |
| Text | `#1D2420` | `#E9EEEA` | |
| Muted text | `#6B7068` | `#9AA49D` | |
| Warning / error | amber and red from the theme, never emerald or sand | | |

Contrast (WCAG): text 15.8 (light) / 15.6 (dark); muted 5.1 / 7.1; white on action 5.4; dark text on the dark action 8.3;
white on the dark Checkout 5.3; text on selected 13.7 / 12.2. All texts meet AA (4.5).

## Type

- Screens: Segoe UI (Windows, Latin and Arabic).
- The logo's MATJAR: Georgia, spaced capitals.

## Where it is used

The window title, the shell header, the sign-in screen, the executable and shortcut icon, the installer, the Windows "Apps"
list; the themes of MISS-08 (light and dark) take their colours from this sheet. Receipts stay black on white.
