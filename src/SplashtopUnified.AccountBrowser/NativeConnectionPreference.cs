namespace SplashtopUnified.AccountBrowser;

/// <summary>
/// A narrow adapter for the documented Global-console connection chooser.
/// It never changes a stored preference and only activates the native option
/// after a short-lived, trusted Connect gesture.
/// </summary>
public static class NativeConnectionPreference
{
    public const int ScriptVersion = 1;
    public const string MessageSource = "splashtop-native-connection-preference";
    public static readonly TimeSpan ArmLifetime = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Install at document start. Its page-posted events are advisory and must never
    /// authorize a native handoff; the native host confirms unverified gestures itself.
    /// </summary>
    public const string InstallScript = """
(() => {
  "use strict";

  const VERSION = 1;
  const SOURCE = "splashtop-native-connection-preference";
  const ARM_MS = 12000;
  const MAX_MUTATIONS = 2048;
  const MAX_SEMANTIC_NODES = 12000;
  const CONNECT_LABEL = "Connect";
  const HEADING_LABEL = "Connect to this Computer";
  const NATIVE_LABEL = "From the Splashtop Business App";
  const BROWSER_LABEL = "From the Web App in this browser";

  let observer = null;
  let timer = 0;
  let armedUntil = 0;
  let mutationCount = 0;
  let scheduled = false;
  let runId = 0;

  function isOfficialGlobalTopDocument() {
    let currentUrl;
    try { currentUrl = new URL(window.location.href); } catch (_) { return false; }
    return window.top === window.self &&
      currentUrl.protocol === "https:" &&
      currentUrl.hostname.toLowerCase() === "my.splashtop.com" &&
      currentUrl.port === "" &&
      currentUrl.username === "" &&
      currentUrl.password === "";
  }

  function post(type, reason) {
    const message = { source: SOURCE, version: VERSION, type };
    if (reason) message.reason = reason;
    try {
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(message);
      } else {
        window.dispatchEvent(new CustomEvent("splashtop-native-connection-preference", { detail: message }));
      }
    } catch (_) {
      // The page may be navigating or the host bridge may not be available.
    }
  }

  function normalize(value) {
    return String(value || "").replace(/\s+/g, " ").trim();
  }

  function isVisible(element) {
    if (!(element instanceof Element) || !element.isConnected) return false;
    for (let node = element; node instanceof Element; node = node.parentElement) {
      if (node.hasAttribute("hidden") || node.getAttribute("aria-hidden") === "true") return false;
      const style = getComputedStyle(node);
      if (style.display === "none" || style.visibility === "hidden" || style.visibility === "collapse" || Number(style.opacity) === 0) return false;
    }
    if (element.getClientRects().length === 0) return false;
    const rect = element.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0 || rect.bottom <= 0 || rect.right <= 0 || rect.top >= innerHeight || rect.left >= innerWidth) return false;
    const hit = document.elementFromPoint(Math.max(0, Math.min(innerWidth - 1, rect.left + rect.width / 2)), Math.max(0, Math.min(innerHeight - 1, rect.top + rect.height / 2)));
    return hit !== null && (hit === element || element.contains(hit));
  }

  function isActionable(element) {
    if (!(element instanceof Element)) return false;
    const tag = element.tagName.toLowerCase();
    const role = normalize(element.getAttribute("role")).toLowerCase();
    const isButton = tag === "button";
    const isLink = tag === "a";
    const hasButtonRole = role === "button";
    if (!isButton && !isLink && !hasButtonRole) return false;
    if (element.hasAttribute("disabled") || element.matches(":disabled") || element.getAttribute("aria-disabled") === "true") return false;
    if (isLink) {
      if (!element.hasAttribute("href") && !hasButtonRole) return false;
      try {
        const target = new URL(element.href, location.href);
        if (target.protocol !== "https:" || target.origin !== location.origin) return false;
      } catch (_) {
        return false;
      }
    }
    return true;
  }

  function hasExactActionLabel(action, expected) {
    if (action.hasAttribute("aria-label")) return normalize(action.getAttribute("aria-label")) === expected;
    if (normalize(action.innerText || action.textContent) === expected) return true;
    for (const descendant of action.querySelectorAll("*")) {
      if (isVisible(descendant) && normalize(descendant.innerText || descendant.textContent) === expected) return true;
    }
    return false;
  }

  function exactLabelActionCount(actions, label) {
    let count = 0;
    for (const action of actions) {
      if (isActionable(action) && isVisible(action) && hasExactActionLabel(action, label)) count++;
    }
    return count;
  }

  function stop() {
    runId++;
    if (observer) observer.disconnect();
    observer = null;
    if (timer) clearTimeout(timer);
    timer = 0;
    armedUntil = 0;
    scheduled = false;
  }

  function fail(reason) {
    stop();
    post("failed", reason);
  }

  function snapshot() {
    const dialogs = document.querySelectorAll('[role=dialog],[aria-modal=true]');
    if (dialogs.length > 16) return { tooManyNodes: true };
    let semanticCount = 0;
    const matchingDialogs = [];
    for (const dialog of dialogs) {
      if (!isVisible(dialog)) continue;
      const semantic = dialog.querySelectorAll("h1,h2,h3,h4,h5,h6,[role],button,a");
      semanticCount += semantic.length;
      if (semanticCount > MAX_SEMANTIC_NODES) return { tooManyNodes: true };
      const headings = [];
      const actions = [];
      for (const element of semantic) {
        if (element.matches("h1,h2,h3,h4,h5,h6") || normalize(element.getAttribute("role")).toLowerCase() === "heading") {
          if (isVisible(element) && normalize(element.innerText || element.textContent) === HEADING_LABEL) headings.push(element);
        }
        if (element.matches("button,a") || normalize(element.getAttribute("role")).toLowerCase() === "button") actions.push(element);
      }
      if (headings.length > 1) return { ambiguous: true };
      if (headings.length === 1) matchingDialogs.push({ dialog, actions });
    }
    if (matchingDialogs.length > 1) return { ambiguous: true };
    if (matchingDialogs.length === 0) return { headingCount: 0, nativeCount: 0, browserCount: 0 };
    const actions = matchingDialogs[0].actions;
    const nativeActions = actions.filter(action =>
      isActionable(action) && isVisible(action) && hasExactActionLabel(action, NATIVE_LABEL));
    return {
      headingCount: 1,
      nativeCount: exactLabelActionCount(actions, NATIVE_LABEL),
      browserCount: exactLabelActionCount(actions, BROWSER_LABEL),
      nativeAction: nativeActions.length === 1 ? nativeActions[0] : null
    };
  }

  function scan(expiredByTimer, expectedRun) {
    if (expectedRun !== runId || !armedUntil) return;
    if (!isOfficialGlobalTopDocument()) return fail("The trusted-click page context changed before the chooser appeared.");
    if (expiredByTimer || Date.now() >= armedUntil) return fail("The documented chooser did not appear within the trusted-click window.");

    let evidence;
    try {
      evidence = snapshot();
    } catch (_) {
      return fail("The chooser could not be inspected safely.");
    }
    if (evidence.tooManyNodes) return fail("The page exceeded the bounded chooser-inspection limit.");
    if (evidence.ambiguous) return fail("The documented chooser was ambiguous; no option was selected.");
    if (evidence.headingCount > 1 || evidence.nativeCount > 1 || evidence.browserCount > 1) {
      return fail("The documented chooser was ambiguous; no option was selected.");
    }
    if (evidence.headingCount !== 1 || evidence.nativeCount !== 1 || evidence.browserCount !== 1) return;

    const nativeAction = evidence.nativeAction ? [evidence.nativeAction] : [];
    if (nativeAction.length !== 1) return fail("The native chooser action was not unique and visible; no option was selected.");

    stop();
    post("attempting");
    try {
      nativeAction[0].click();
      post("activated");
    } catch (_) {
      post("failed", "The native chooser action could not be activated.");
    }
  }

  function scheduleScan(expectedRun) {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(() => {
      scheduled = false;
      scan(false, expectedRun);
    });
  }

  function beginTrustedConnect() {
    stop();
    armedUntil = Date.now() + ARM_MS;
    mutationCount = 0;
    const thisRun = runId;
    post("armed");
    observer = new MutationObserver(records => {
      mutationCount += records.length;
      if (mutationCount > MAX_MUTATIONS) return fail("Chooser observation exceeded its bounded mutation limit.");
      scheduleScan(thisRun);
    });
    observer.observe(document.documentElement, {
      subtree: true,
      childList: true,
      characterData: true,
      attributes: true,
      attributeFilter: ["aria-label", "aria-hidden", "class", "style", "hidden", "disabled", "href", "role"]
    });
    timer = setTimeout(() => scan(true, thisRun), ARM_MS);
    scheduleScan(thisRun);
  }

  function eventAction(event) {
    const path = typeof event.composedPath === "function" ? event.composedPath() : [event.target];
    for (const item of path) {
      if (isActionable(item) && hasExactActionLabel(item, CONNECT_LABEL)) return item;
    }
    return null;
  }

  document.addEventListener("click", event => {
    if (event.isTrusted !== true || !isOfficialGlobalTopDocument()) return;
    const action = eventAction(event);
    if (!action || !isVisible(action)) return;
    beginTrustedConnect();
  }, true);
})();
""";

    /// <summary>Returns true only for a trusted Connect click at the Global origin.</summary>
    public static bool TryArm(
        string? origin,
        bool isTrustedConnectClick,
        DateTimeOffset now,
        out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (!isTrustedConnectClick || !IsOfficialGlobalOrigin(origin))
        {
            return false;
        }

        expiresAt = now + ArmLifetime;
        return true;
    }

    /// <summary>
    /// Validates a semantic chooser snapshot without performing DOM or host actions.
    /// Counts must represent visible semantic headings/actions with exact documented labels.
    /// </summary>
    public static NativeChooserDecision Evaluate(
        string? origin,
        DateTimeOffset? armedUntil,
        DateTimeOffset now,
        NativeChooserEvidence evidence,
        bool observationBudgetExpired = false)
    {
        if (armedUntil is null)
        {
            return NativeChooserDecision.NotArmed;
        }

        if (!IsOfficialGlobalOrigin(origin) || evidence.VisibleHeadingCount < 0 ||
            evidence.VisibleNativeActionCount < 0 || evidence.VisibleBrowserActionCount < 0)
        {
            return NativeChooserDecision.Fail;
        }

        if (now >= armedUntil.Value || observationBudgetExpired)
        {
            return NativeChooserDecision.Fail;
        }

        if (evidence.VisibleHeadingCount > 1 || evidence.VisibleNativeActionCount > 1 || evidence.VisibleBrowserActionCount > 1)
        {
            return NativeChooserDecision.Fail;
        }

        if (evidence.VisibleHeadingCount == 1 &&
            evidence.VisibleNativeActionCount == 1 &&
            evidence.VisibleBrowserActionCount == 1)
        {
            return NativeChooserDecision.ActivateNative;
        }

        return NativeChooserDecision.Wait;
    }

    public static bool IsOfficialGlobalOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Any(char.IsControl) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.IdnHost, "my.splashtop.com", StringComparison.OrdinalIgnoreCase) &&
               uri.IsDefaultPort &&
               uri.UserInfo.Length == 0 &&
               (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/") &&
               uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }
}

public enum NativeChooserDecision
{
    NotArmed,
    Wait,
    ActivateNative,
    Fail
}

public readonly record struct NativeChooserEvidence(
    int VisibleHeadingCount,
    int VisibleNativeActionCount,
    int VisibleBrowserActionCount);
