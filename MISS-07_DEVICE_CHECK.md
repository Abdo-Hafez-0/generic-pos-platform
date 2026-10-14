# MISS-07d - Device check (for the shop owner or technician)

GenericPOS has been tested without real devices: with simulated printers, a printer "file", a closed network port, and keyboard input
that looks like a scanner. This check is the part only real devices can prove. Do it once on the shop's PC with the shop's own
devices, before the first day of selling, and again after changing a device.

It takes about one hour. Fill in the **Result** column (OK / not OK + what you saw) and send the filled list back (see section 7).

**Supported devices (decided 2026-10-14)**

| Device | Supported | Not supported yet |
| --- | --- | --- |
| Receipt printer | ESC/POS thermal printers (Epson TM, Xprinter, Rongta, Bixolon, ...) by **USB with the maker's Windows driver**, or on the **network** (port 9100) | Serial (COM) printers; printers without ESC/POS |
| Cash drawer | Plugged into the receipt printer (RJ11/RJ12 "DK" port) | Drawers on their own USB or serial cable |
| Barcode scanner | USB scanners in **keyboard mode** (the factory setting of almost all scanners), ending each code with Enter | Scanners in serial/COM or "USB-HID POS" mode |
| Label printer | ZPL printers (Zebra and compatible) by USB with the Windows driver, or on the network | EPL/TSPL-only printers |
| Scale | - | Not supported for now (user decision) |

---

## 1. What you need

- The shop PC with GenericPOS installed and activated, and the owner's account.
- At least one product with a barcode and stock, and an opened till (Point of sale > Open till). A second product with letters in its
  barcode (Code 128, e.g. `ABC-123`) if you have one.
- The devices, their cables, paper rolls (and one almost empty roll if you have one), the drawer key.
- A phone to take photos of the printed receipts.

## 2. Optional: before the devices arrive

These are optional. They prove the Windows side works before the printer is there.

1. **"Generic / Text Only" printer.** In Windows Settings > Printers & scanners, add a printer by hand, choose the
   *Generic / Text Only* driver and the port *FILE: (Print to File)*. Then in GenericPOS: Administration > Devices > Receipt printer >
   *Printer installed in Windows (USB)*, choose that printer, and press **Print a test receipt**. Windows asks for a file name. The file
   holds the receipt's printer codes. Getting the file proves the Windows printing path works. Remove that printer from Windows
   afterwards. *(This changes Windows settings, which is why it is for you to do and is optional.)*
2. **The automated printer test** (for a technician with the source code). Once the real printer is installed in Windows, this prints a
   test receipt and opens the drawer:

   ```
   set GENERICPOS_TEST_PRINTER=<the printer's name as Windows shows it>
   dotnet test tests\Hardware.Tests --filter "FullyQualifiedName~A_real_printer"
   ```

## 3. Setting the devices up

1. **USB printers:** install the maker's Windows driver first, and print the Windows test page from Windows (Printers & scanners >
   the printer > Print a test page). If the Windows test page does not print, GenericPOS cannot print either. Fix that first.
2. **Network printers:** give the printer a fixed address (printer menu or the maker's tool). Print the printer's own self-test page
   (usually: hold FEED while switching it on). It shows the address.
3. In GenericPOS: **Administration > Devices**.
   - Receipt printer: *Printer installed in Windows (USB)* + choose its name, or *Network printer* + address + port (9100).
   - Characters per line: *32 - 58 mm paper* or *42 - 80 mm paper* (*48* if your 80 mm printer uses small letters).
   - Cut the paper after each receipt: on if the printer has a cutter.
   - Cash drawer: *Connected to the receipt printer*, *Pin 2 (most drawers)*.
   - Barcode scanner: *USB scanner in keyboard mode*.
   - Label printer: as for the receipt printer, if you have one.
4. Press **Check the devices**, then **Save**, then **Restart now**. After the restart, open Devices again: the choices must still be there.

## 4. The check

| # | Do this | Expected | Result |
| --- | --- | --- | --- |
| **A** | **Receipt printer** | | |
| A1 | Devices > **Check the devices** | Receipt printer: *Ready* | |
| A2 | **Print a test receipt** | A receipt comes out at once. Nothing is cut off on the right, the text is straight and readable, the paper is cut (if a cutter). Take a photo | |
| A3 | Sell one item with cash (Point of sale) | The receipt prints after the sale: shop name, item, total, cash tendered, change, cashier, date and time **on the shop's clock** | |
| A4 | Put an Arabic shop name or footer in the receipt settings, sell again | The Arabic text is printed as a picture, readable and right-to-left, not cut off at the right edge (58 mm too) | |
| A5 | **Print the receipt again** (below the cart) | The same receipt with `*** COPY ***` under the heading, with the payment | |
| A6 | **Recent sales...** > choose an older sale > **Print this receipt again** | That sale's receipt, marked COPY | |
| **B** | **Cash drawer** | | |
| B1 | Devices > **Open the drawer** | The drawer opens | |
| B2 | A cash sale | The drawer opens after the sale | |
| B3 | A card sale | The drawer does **not** open | |
| B4 | If B1 did not open: choose *Pin 5*, press **Open the drawer** again | Opens: keep Pin 5, Save, Restart | |
| **C** | **Barcode scanner** | | |
| C1 | Devices: click on an empty area (not in a text box), scan a product | *Read ... (13 characters) at ...* with the number printed under the barcode | |
| C2 | Point of sale: scan the product with the cursor in the *Barcode / SKU* box, then again after clicking the cart list | Each scan adds the product once (quantity 2 in all). The scanner's Enter never presses *Checkout* or another button | |
| C3 | Scan a barcode with letters (Code 128) | Read correctly, upper/lower case as printed | |
| C4 | Switch Windows to the **Arabic keyboard** (Win+Space), repeat C1 and C3 | Same codes as in C1/C3 (no Arabic letters instead of Latin ones) | |
| C5 | Scan ten items quickly one after the other | Ten scans, none lost, none merged | |
| C6 | Type a barcode by hand in the code box and press Enter | Works as before (typing is not mistaken for a scan) | |
| **D** | **Label printer** (if any) | | |
| D1 | Devices > **Print a test label** | A label with a barcode; the barcode is read by the scanner | |

## 5. The failure campaign

The rule being checked: **a device problem never loses or spoils a sale.** The sale is saved first. Then the cashier is told in plain
words what went wrong, e.g. *"The sale was completed and saved, but the receipt could not be printed: ..."*. The receipt can then be
printed again.

For every line, write down: the message on the screen (word for word or a photo), whether the sale is in Sales history, and whether
a receipt came out **later by itself** (this matters: Windows may keep the receipt waiting and print it when the printer comes back, and
a cashier who printed it again would then give two).

| # | Do this | Expected | Result |
| --- | --- | --- | --- |
| F1 | Switch the receipt printer **off**, sell | Sale saved; message that the receipt was not printed (if Windows knows the printer is off). Switch on: note whether the receipt comes out by itself | |
| F2 | Switch the printer off **while** it prints (a long receipt: many lines) | Sale saved; note what printed and what the screen said | |
| F3 | **Paper out** (or the almost-empty roll), sell | Message says *out of paper* or *not printed*; after new paper, **Print the receipt again** works | |
| F4 | **Cover open**, sell | Message (*cover open* / *not printed*); after closing it, reprint works | |
| F5 | **Unplug the USB cable**, sell; plug back in | Sale saved; message; after plugging back, Devices > Check shows *Ready* without restarting GenericPOS | |
| F6 | Windows: the printer > *Pause printing*, sell; resume | Message *is paused in Windows*; note whether the receipt printed after resuming | |
| F7 | Windows: the printer > *Use Printer Offline*, sell; switch it back | Message *is set to 'Use Printer Offline' in Windows* | |
| F8 | Network printer: **unplug its network cable**, sell | After about 3 seconds: sale saved, message *did not respond in time* or *not available*; the till is not frozen | |
| F9 | Unplug the **drawer** cable, cash sale | Sale saved; nothing hangs (the drawer cannot report this, so maybe no message: note what happens) | |
| F10 | **Unplug the scanner**, plug it back in, scan | Scans work again without restarting GenericPOS | |
| F11 | Scanner set to end with **Tab** instead of Enter (if its manual allows; set it back after!) | The code is not taken as a scan: note what happens (we then know which setting to tell shops) | |
| F12 | **Power loss**: during a sale (cart filled, *Checkout* just pressed), switch the PC off at the wall. *Only on a PC with nothing else open; a laptop: hold the power button* | After restart: the sale is either completed (in Sales history, stock reduced, cash in the drawer shift) or not at all, never half. **Recent sales...** shows it if completed, and its receipt can be printed | |

## 6. Known limits (no need to report these)

- When a USB printer is switched off, some Windows drivers do not tell Windows. GenericPOS then cannot know either: the receipt waits
  in the Windows queue and prints when the printer comes back. F1/F2 find out how your printer behaves.
- A drawer plugged into the printer cannot say whether it opened.
- Saved device settings take effect after **Restart now** (never in the middle of a sale); the Devices test buttons use the settings as entered.
- Product labels cannot be printed from a screen yet: the label printer is only checked with the test label (D1).

## 7. What to send back

1. This list with the Result column filled in.
2. The photos of the receipts (A2-A6, and anything odd).
3. The exact models: printer, drawer, scanner, label printer, and the printer's name as Windows shows it.
4. A support file: **Administration > Support > Save support file...** (it holds the logs of the check; no passwords or keys).
