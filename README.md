# DropFly

Share from your Pixel to your Windows laptop: Share -> DropFly, and a card flies off
the top of the phone and drops into the top of the laptop screen.

## Build it (no developer tools needed)
1. Create a free account at github.com, then click **New repository** (any name, Public or Private).
2. On the new repo page click **uploading an existing file** and drag in everything from this folder
   (the `android` folder, the `windows` folder and the `.github` folder).
   If `.github` doesn't upload: click **Add file -> Create new file**, type
   `.github/workflows/build.yml` as the name, and paste in the contents of that file. Then commit.
3. Open the **Actions** tab. A run called "Build DropFly" starts by itself (about 3-5 minutes).
4. Open the finished run, scroll to **Artifacts**, and download
   `DropFly-Android-APK` and `DropFly-Windows-EXE` (they are zips; unzip them).
5. If a run is red, open it, copy the error text and send it to Claude.

## Install
- Laptop: run `DropFly.exe`. It sits in the system tray and shows a 6-digit code on first start.
  Allow it through the firewall on Private networks.
- Phone: copy the APK over (Drive, cable, whatever), open it, allow "install unknown apps" once.
  Open DropFly, type the code, Save, Test connection.
- After that: in Photos / Files / Chrome, tap Share -> DropFly.

Files land in `Downloads\DropFly` on the laptop. Shared text is copied to the clipboard.
Phone and laptop must be on the same Wi-Fi.
