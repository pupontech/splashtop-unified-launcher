# Official connection chooser evidence

Source: https://support-splashtopbusiness.splashtop.com/hc/en-us/articles/4630897874203-Web-App
Official screenshot viewed: https://support-splashtopbusiness.splashtop.com/hc/article_attachments/54288385918491

Visible heading: Connect to this Computer.
Native choice exact label: From the Splashtop Business App.
Native description: Provides the fullest Splashtop experience; Installation required.
Browser choice exact label: From the Web App in this browser.
No always-use checkbox is visible in this screenshot. It is evidence of UI text/choice only, not DOM selectors or a stored preference API.

A prototype can prefer the native option using a narrow semantic text match when this exact chooser is shown after a user connects. Fail visibly if the expected chooser/native option is missing or ambiguous; do not select web app fallback or fabricate a private setting. Such automation must be tested against actual WebView2 events with fake dispatcher and preserve trusted click provenance for script-triggered follow-up.

Official source says web-app availability is controlled by team owner; do not modify team settings as part of this app preference.
