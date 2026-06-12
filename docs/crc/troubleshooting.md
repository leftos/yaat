# Troubleshooting

## Known Issues and Solutions

Before submitting a bug report, please see if your issue has been identified and solved below:

#### CRC crashes to desktop while screen-sharing on Discord

This is due to a known bug in Discord's screen-sharing that also affects official Microsoft applications. To screen-share CRC on Discord without causing it to crash, the **Use our advanced technology to capture your screen** option must be **disabled** in Discord's **Voice & Video** options.

#### CRC fails to start due to an HTTP timeout

This is due to CRC being unable to reach the internet. Ensure you are connected to the internet and CRC is allowed through any antivirus software or firewall rules you may have enabled.

#### Displays are blank/missing elements

This is most likely due to your graphics driver being misconfigured. Ensure your graphics driver supports OpenGL and is up to date. Additionally, ensure your graphics configuration is set to its default settings including:

- Disabling Nvidia G-Sync
- Disabling antialiasing (or allowing application control)
- Disabling vertical sync (or allowing application control)

#### ASDE-X aural alerts do not sound

This may be caused by European versions of Windows (Windows-N) [not shipping with certain media playing capabilities](https://en.wikipedia.org/wiki/Microsoft_Corp._v._Commission). Follow [this guide](https://support.microsoft.com/en-us/topic/media-feature-pack-list-for-windows-n-editions-c1c6fffa-d052-8338-7a79-a4bb980a700a) to enable these features.

## Submitting a Bug Report

If you are still unable to resolve your issue, please file a bug report in the [CRC Bug Reports](discord://discord.com/channels/953714419597201408/1022954972712812626) forum on the [vNAS Discord](https://discord.gg/MFtQbd9Svs).
