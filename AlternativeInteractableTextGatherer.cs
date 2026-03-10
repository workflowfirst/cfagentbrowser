using System.Text;
using PuppeteerSharp;

public static class AlternativeInteractableTextGatherer
{
    public sealed class InteractableElement
    {
        public string Name { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public bool IsInteractable { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
    }

    private sealed class RawCandidate
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ViewportWidth { get; set; }
        public int ViewportHeight { get; set; }
        public string PlainText { get; set; } = "";
        public string ContextText { get; set; } = "";
        public string Placeholder { get; set; } = "";
        public string AriaLabel { get; set; } = "";
        public string Title { get; set; } = "";
        public string NameAttr { get; set; } = "";
        public string LabelText { get; set; } = "";
        public string InputType { get; set; } = "";
        public string Value { get; set; } = "";
        public string NearbyText { get; set; } = "";
        public bool IsInteractable { get; set; }
        public bool IsInputLike { get; set; }
        public bool IsDropdown { get; set; }
        public bool IsTextEntry { get; set; }
        public bool IsDisabled { get; set; }
        public bool IsTableContext { get; set; }
    }

    public static async Task<IReadOnlyList<InteractableElement>> CollectAsync(
        IPage page,
        int maxElements = 1200,
        int stepPixels = 10,
        int maxRadiusPixels = 120,
        bool unrestrictedText = false)
    {
        var items = await page.EvaluateFunctionAsync<RawCandidate[]>(@"(maxElements, stepPx, maxRadiusPx) => {
  const norm = (s) => {
    if (!s) return '';
    return String(s).replace(/\s+/g, ' ').trim();
  };

  const WffAppRules = {
    applyLabelTextFromLabels: () => {
      try {
        const labels = document.querySelectorAll('label');
        for (const label of labels) {
          const targetId = norm(label.getAttribute && label.getAttribute('for'));
          if (!targetId) continue;
          const target = document.getElementById(targetId);
          if (!target) continue;

          let txt = '';
          try {
            const clone = label.cloneNode(true);
            const pops = clone.querySelectorAll('.toolTipPopup');
            for (const p of pops) p.remove();
            txt = norm(clone.innerText || clone.textContent || '');
          } catch {
            txt = norm(label.innerText || label.textContent || '');
          }

          if (txt) target.setAttribute('label-text', txt);
        }
      } catch {}
    },

    getScopeRoot: () => {
      try {
        const isVisibleContainer = (el) => {
          try {
            if (!el || el.nodeType !== 1) return false;
            const cs = window.getComputedStyle(el);
            if (!cs) return false;
            if (cs.display === 'none' || cs.visibility === 'hidden' || cs.visibility === 'collapse') return false;
            const op = parseFloat(cs.opacity || '1');
            if (Number.isFinite(op) && op <= 0) return false;
            const r = el.getBoundingClientRect();
            if (!r || r.width < 2 || r.height < 2) return false;
            const vw = Math.max(0, window.innerWidth || 0);
            const vh = Math.max(0, window.innerHeight || 0);
            return r.right > 0 && r.bottom > 0 && r.left < vw && r.top < vh;
          } catch {
            return false;
          }
        };

        const hasMeaningfulContent = (el) => {
          try {
            if (!el || el.nodeType !== 1) return false;
            if (!(el.querySelector && el.querySelector('*'))) return false;

            const txt = norm(el.innerText || el.textContent || '');
            if (txt.length > 0) return true;

            // Allow widget-like containers that may be text-light but actionable.
            const actionable = el.querySelector('input:not([type=hidden]), textarea, select, button, a[href], [role=button], [role=menuitem], [contenteditable=true]');
            return !!actionable;
          } catch {
            return false;
          }
        };

        const pick = (id) => {
          const el = document.getElementById(id);
          if (!el) return null;
          if (!isVisibleContainer(el)) return null;
          if (!hasMeaningfulContent(el)) return null;
          return el;
        };

        return pick('inlineact')
          || pick('editDDRow')
          || pick('dropdown')
          || document.documentElement;
      } catch {
        return document.documentElement;
      }
    },

    findLabelTextNear: (el) => {
      if (!el || el.nodeType !== 1) return '';
      const getOwn = (n) => norm(n && n.getAttribute && n.getAttribute('label-text'));

      // self
      let v = getOwn(el);
      if (v) return v;

      // parents
      let p = el.parentElement;
      for (let i = 0; p && i < 10; i++, p = p.parentElement) {
        v = getOwn(p);
        if (v) return v;
      }

      // children/descendants
      try {
        const n = el.querySelector('[label-text]');
        v = getOwn(n);
        if (v) return v;
      } catch {}

      return '';
    }
  };

  const textSansExcluded = (el) => {
    try {
      if (!el || el.nodeType !== 1) return '';
      const clone = el.cloneNode(true);
      const remove = clone.querySelectorAll('option,optgroup,select,.ToolText');
      for (const n of remove) n.remove();
      return norm(clone.innerText || clone.textContent || '');
    } catch {
      try {
        return norm(el.innerText || el.textContent || '');
      } catch {
        return '';
      }
    }
  };

  // App-specific preprocessing first.
  WffAppRules.applyLabelTextFromLabels();

  const clamp = (v, min, max) => Math.min(Math.max(v, min), max);

  const isVisible = (el) => {
    try {
      if (!el || el.nodeType !== 1) return false;
      if (el.getAttribute && el.getAttribute('aria-hidden') === 'true') return false;
      const cs = window.getComputedStyle(el);
      if (!cs) return false;
      if (cs.display === 'none' || cs.visibility === 'hidden' || cs.visibility === 'collapse') return false;
      if (cs.pointerEvents === 'none') return false;
      const op = parseFloat(cs.opacity || '1');
      if (Number.isFinite(op) && op <= 0) return false;
      return true;
    } catch {
      return false;
    }
  };

  const isExcludedElement = (el) => {
    try {
      if (!el || el.nodeType !== 1) return true;
      const tag = String(el.tagName || '').toLowerCase();
      if (tag === 'option' || tag === 'optgroup') return true;
      if (el.classList && el.classList.contains('ToolText')) return true;
      return false;
    } catch {
      return true;
    }
  };

  const isDisabledNear = (el) => {
    try {
      if (!el || el.nodeType !== 1) return false;
      if (el.disabled === true) return true;
      const ad = String(el.getAttribute && el.getAttribute('aria-disabled') || '').toLowerCase();
      if (ad === 'true') return true;
      if (el.hasAttribute && el.hasAttribute('disabled')) return true;
      let p = el.parentElement;
      for (let i = 0; p && i < 8; i++, p = p.parentElement) {
        if (p.disabled === true) return true;
        const adp = String(p.getAttribute && p.getAttribute('aria-disabled') || '').toLowerCase();
        if (adp === 'true') return true;
        if (p.hasAttribute && p.hasAttribute('disabled')) return true;
      }
    } catch {}
    return false;
  };

  const centerPoint = (el) => {
    try {
      if (!isVisible(el)) return null;
      const r = el.getBoundingClientRect();
      if (!r || r.width < 2 || r.height < 2) return null;
      const vw = Math.max(0, window.innerWidth || 0);
      const vh = Math.max(0, window.innerHeight || 0);

      const ix1 = Math.max(0, r.left);
      const iy1 = Math.max(0, r.top);
      const ix2 = Math.min(vw, r.right);
      const iy2 = Math.min(vh, r.bottom);
      if (ix2 <= ix1 || iy2 <= iy1) return null;

      let x = Math.round(ix1 + (ix2 - ix1) / 2);
      let y = Math.round(iy1 + (iy2 - iy1) / 2);
      x = clamp(x, 1, Math.max(1, vw - 2));
      y = clamp(y, 1, Math.max(1, vh - 2));

      // Occlusion filter: keep only if at least one sample point can see this element.
      const samplePoints = [
        [x, y],
        [Math.round(ix1 + 1), Math.round(iy1 + 1)],
        [Math.round(ix2 - 1), Math.round(iy1 + 1)],
        [Math.round(ix1 + 1), Math.round(iy2 - 1)],
        [Math.round(ix2 - 1), Math.round(iy2 - 1)],
      ];
      let hasVisibleHit = false;
      for (const p of samplePoints) {
        const sx = clamp(p[0], 1, Math.max(1, vw - 2));
        const sy = clamp(p[1], 1, Math.max(1, vh - 2));
        let hit = null;
        try { hit = document.elementFromPoint(sx, sy); } catch { hit = null; }
        if (!hit) continue;
        if (hit === el || el.contains(hit) || hit.contains(el)) {
          hasVisibleHit = true;
          break;
        }
      }
      if (!hasVisibleHit) return null;

      return {
        xDoc: Math.round(x + (window.scrollX || 0)),
        yDoc: Math.round(y + (window.scrollY || 0)),
        r
      };
    } catch {
      return null;
    }
  };

  const isInputLike = (el) => {
    if (!el || el.nodeType !== 1) return false;
    const tag = String(el.tagName || '').toLowerCase();
    if (tag === 'textarea' || tag === 'select') return true;
    if (tag === 'input') {
      const t = String(el.getAttribute('type') || 'text').toLowerCase();
      return t !== 'hidden';
    }
    return el.isContentEditable === true || String(el.getAttribute('role') || '').toLowerCase() === 'textbox';
  };

  const nearestInput = (el) => {
    if (!el || el.nodeType !== 1) return null;
    if (isInputLike(el)) return el;
    return el.querySelector('input:not([type=hidden]), textarea, select, [contenteditable=true], [role=textbox]');
  };

  const isNativeInteractable = (el) => {
    if (!el || el.nodeType !== 1) return false;
    const tag = String(el.tagName || '').toLowerCase();
    if (tag === 'a' && !!(el.getAttribute && el.getAttribute('href'))) return true;
    if (tag === 'button') return !(el.disabled === true);
    if (tag === 'select' || tag === 'textarea') return !(el.disabled === true);
    if (tag === 'input') {
      const t = String(el.getAttribute('type') || 'text').toLowerCase();
      if (t === 'hidden') return false;
      return !(el.disabled === true);
    }

    const role = String(el.getAttribute && el.getAttribute('role') || '').toLowerCase();
    if (role === 'button' || role === 'link' || role === 'menuitem' || role === 'tab') return true;
    if (role === 'textbox' || role === 'combobox' || role === 'checkbox' || role === 'radio' || role === 'switch') return true;

    if (el.hasAttribute && el.hasAttribute('onclick')) return true;
    return false;
  };

  const closestWithText = (start) => {
    let cur = start;
    while (cur && cur.nodeType === 1) {
      const t = textSansExcluded(cur) || norm(cur.getAttribute && cur.getAttribute('aria-label')) || norm(cur.title) || '';
      if (t) return t;
      cur = cur.parentElement;
    }
    return '';
  };

  const pointText = (x, y) => {
    const vw = Math.max(0, window.innerWidth || 0);
    const vh = Math.max(0, window.innerHeight || 0);
    const px = clamp(Math.round(x), 1, Math.max(1, vw - 2));
    const py = clamp(Math.round(y), 1, Math.max(1, vh - 2));
    const el = document.elementFromPoint(px, py);
    if (!el) return '';
    return closestWithText(el);
  };

  const nearbyInputLabel = (r, stepPx, maxRadiusPx) => {
    if (!r) return '';

    // Preferred first probe: just above-left.
    const first = pointText(r.left - stepPx, r.top - stepPx);
    if (first) return first;

    for (let radius = stepPx; radius <= maxRadiusPx; radius += stepPx) {
      const left = r.left - radius;
      const top = r.top - radius;
      const right = r.right + radius;
      const bottom = r.bottom + radius;

      // Anti-clockwise ring: left edge down, bottom edge right, right edge up, top edge left.
      for (let y = top; y <= bottom; y += stepPx) {
        const t = pointText(left, y);
        if (t) return t;
      }
      for (let x = left + stepPx; x <= right; x += stepPx) {
        const t = pointText(x, bottom);
        if (t) return t;
      }
      for (let y = bottom - stepPx; y >= top; y -= stepPx) {
        const t = pointText(right, y);
        if (t) return t;
      }
      for (let x = right - stepPx; x >= left; x -= stepPx) {
        const t = pointText(x, top);
        if (t) return t;
      }
    }

    return '';
  };

  const hoverSelectors = [];
  try {
    for (const sheet of Array.from(document.styleSheets || [])) {
      let rules = null;
      try { rules = sheet.cssRules; } catch { rules = null; }
      if (!rules) continue;
      for (const rule of Array.from(rules)) {
        if (!rule || !rule.selectorText) continue;
        const selectorText = String(rule.selectorText);
        if (!selectorText.includes(':hover')) continue;
        for (const sel of selectorText.split(',')) {
          const raw = sel.trim();
          if (!raw || !raw.includes(':hover')) continue;
          const base = raw.replace(/:hover/g, '').trim();
          if (base) hoverSelectors.push(base);
        }
      }
    }
  } catch {}

  const hasHoverRule = (el) => {
    if (!el || !hoverSelectors.length) return false;
    for (const sel of hoverSelectors) {
      try {
        if (el.matches(sel)) return true;
      } catch {}
    }
    return false;
  };

  const cursorKinds = (el) => {
    const kinds = new Set();
    try {
      const cs = window.getComputedStyle(el);
      const c = String((cs && cs.cursor) || '').toLowerCase();
      if (c === 'pointer') kinds.add('pointer');
      if (c === 'text') kinds.add('text');

      // Some UIs only switch cursor on hover.
      try {
        el.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
        const cs2 = window.getComputedStyle(el);
        const c2 = String((cs2 && cs2.cursor) || '').toLowerCase();
        if (c2 === 'pointer') kinds.add('pointer-hover');
        if (c2 === 'text') kinds.add('text-hover');
        el.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }));
      } catch {}
    } catch {}
    return Array.from(kinds);
  };

  const out = [];
  const scopeRoot = WffAppRules.getScopeRoot();
  const all = scopeRoot.getElementsByTagName('*');
  const cap = Math.min(all.length, 12000);

  for (let i = 0; i < cap; i++) {
    const el = all[i];
    if (!el || !isVisible(el)) continue;
    if (isExcludedElement(el)) continue;

    const nativeInteractable = isNativeInteractable(el);
    const cursors = cursorKinds(el);
    const hover = hasHoverRule(el);
    if (!nativeInteractable && !cursors.length && !hover) continue;

    const pt = centerPoint(el);
    if (!pt) continue;

    const inp = nearestInput(el) || el;
    const plainText = textSansExcluded(el);
    const inpTag = String(inp.tagName || '').toLowerCase();
    const placeholder = norm(inp.getAttribute && inp.getAttribute('placeholder')) || '';
    const ariaLabel = norm(inp.getAttribute && inp.getAttribute('aria-label')) || norm(el.getAttribute && el.getAttribute('aria-label')) || '';
    const title = norm(inp.title) || norm(el.title) || '';
    const nameAttr = norm(inp.getAttribute && inp.getAttribute('name')) || '';
    const labelText = WffAppRules.findLabelTextNear(inp) || WffAppRules.findLabelTextNear(el) || '';
    const inputType = norm(inp.getAttribute && inp.getAttribute('type')).toLowerCase() || '';
    const value = norm(inp.value) || '';
    const nearbyText = nearbyInputLabel(pt.r, stepPx, maxRadiusPx) || '';
    const role = String(inp.getAttribute && inp.getAttribute('role') || '').toLowerCase();
    const isDropdown = (inpTag === 'select' || role === 'combobox');
    const textTypes = new Set(['', 'text', 'search', 'email', 'password', 'url', 'tel', 'number', 'date', 'datetime-local', 'month', 'week', 'time']);
    const isTextEntry = (inpTag === 'textarea')
      || (inpTag === 'input' && textTypes.has(inputType))
      || inp.isContentEditable === true
      || role === 'textbox';

    const isDisabled = isDisabledNear(inp) || isDisabledNear(el);

    let cur = el;
    let isTableContext = false;
    while (cur && cur.nodeType === 1) {
      const tag = String(cur.tagName || '').toLowerCase();
      if (tag === 'td' || tag === 'th' || tag === 'tr' || tag === 'table') {
        isTableContext = true;
        break;
      }
      cur = cur.parentElement;
    }

    out.push({
      plainText,
      contextText: plainText,
      placeholder,
      ariaLabel,
      title,
      nameAttr,
      labelText,
      inputType,
      value,
      nearbyText,
      x: pt.xDoc,
      y: pt.yDoc,
      left: Math.round(pt.r.left || 0),
      top: Math.round(pt.r.top || 0),
      right: Math.round(pt.r.right || 0),
      bottom: Math.round(pt.r.bottom || 0),
      width: Math.round(pt.r.width || 0),
      height: Math.round(pt.r.height || 0),
      viewportWidth: Math.max(0, window.innerWidth || 0),
      viewportHeight: Math.max(0, window.innerHeight || 0),
      isInteractable: true,
      isInputLike: isInputLike(inp),
      isDropdown,
      isTextEntry,
      isDisabled,
      isTableContext
    });

    if (out.length >= maxElements) break;
  }

  // Add visible non-interactable text context.
  for (let i = 0; i < cap; i++) {
    const el = all[i];
    if (!el || !isVisible(el)) continue;
    if (isExcludedElement(el)) continue;

    const cursors = cursorKinds(el);
    const hover = hasHoverRule(el);
    if (cursors.length || hover) continue;

    const pt = centerPoint(el);
    if (!pt) continue;

    const text = textSansExcluded(el);
    if (!text) continue;

    let cur = el;
    let isTableContext = false;
    while (cur && cur.nodeType === 1) {
      const tag = String(cur.tagName || '').toLowerCase();
      if (tag === 'td' || tag === 'th' || tag === 'tr' || tag === 'table') {
        isTableContext = true;
        break;
      }
      cur = cur.parentElement;
    }

    out.push({
      plainText: text,
      contextText: text,
      placeholder: '',
      ariaLabel: norm(el.getAttribute && el.getAttribute('aria-label')) || '',
      title: norm(el.title) || '',
      nameAttr: '',
      labelText: WffAppRules.findLabelTextNear(el) || '',
      inputType: '',
      value: '',
      nearbyText: '',
      x: pt.xDoc,
      y: pt.yDoc,
      left: Math.round(pt.r.left || 0),
      top: Math.round(pt.r.top || 0),
      right: Math.round(pt.r.right || 0),
      bottom: Math.round(pt.r.bottom || 0),
      width: Math.round(pt.r.width || 0),
      height: Math.round(pt.r.height || 0),
      viewportWidth: Math.max(0, window.innerWidth || 0),
      viewportHeight: Math.max(0, window.innerHeight || 0),
      isInteractable: false,
      isInputLike: false,
      isDropdown: false,
      isTextEntry: false,
      isDisabled: isDisabledNear(el),
      isTableContext
    });

    if (out.length >= maxElements * 3) break;
  }

  return out;
}", maxElements, stepPixels, maxRadiusPixels);

        if (items is null || items.Length == 0)
        {
            return Array.Empty<InteractableElement>();
        }

        static string Norm(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return string.Join(' ', s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        var interactables = new List<LabeledCandidate>(items.Length);
        var textOnly = new List<LabeledCandidate>(items.Length / 2);
        const int minTextContextWords = 6;
        const int maxInteractableChars = 120;

        foreach (var c in items)
        {
            string label;
            if (c.IsInteractable)
            {
                if (c.IsInputLike)
                {
                    var isButtonLikeInput = c.InputType.Equals("submit", StringComparison.OrdinalIgnoreCase)
                        || c.InputType.Equals("button", StringComparison.OrdinalIgnoreCase)
                        || c.InputType.Equals("reset", StringComparison.OrdinalIgnoreCase);

                    if (isButtonLikeInput)
                    {
                        label = FirstNonEmpty(c.Value, c.Title, c.AriaLabel, c.Placeholder, c.NameAttr, c.NearbyText, c.PlainText);
                    }
                    else
                    {
                        label = FirstNonEmpty(c.Placeholder, c.Title, c.AriaLabel, c.NameAttr, c.Value, c.NearbyText, c.PlainText);
                    }
                }
                else
                {
                    label = FirstNonEmpty(c.PlainText, c.Title, c.AriaLabel, c.NearbyText, c.Value);
                }

                if (!string.IsNullOrWhiteSpace(c.LabelText) && !label.Contains("(Label:", StringComparison.OrdinalIgnoreCase))
                {
                    label = $"{label} (Label: {Norm(c.LabelText)})";
                }

                if (c.IsDisabled && !label.Contains("(DISABLED)", StringComparison.OrdinalIgnoreCase))
                {
                    label = $"{label} (DISABLED)";
                }
            }
            else
            {
                label = FirstNonEmpty(c.ContextText, c.PlainText, c.Title, c.AriaLabel);
                if (c.IsDisabled && !label.Contains("(DISABLED)", StringComparison.OrdinalIgnoreCase))
                {
                    label = $"{label} (DISABLED)";
                }
            }

            label = Norm(label);
            if (label.Length == 0) continue;
            var wordCount = CountWords(label);
            var wasCropped = false;
            if (c.IsInteractable)
            {
                // Always cap interactable text length, even in full-text mode.
                if (label.Length > maxInteractableChars)
                {
                    label = label[..maxInteractableChars];
                    wasCropped = true;
                }
            }
            else if (!unrestrictedText)
            {
                // Keep richer paragraph context for non-interactable text.
                if (wordCount > 120) continue;
                if (label.Length > 320)
                {
                    label = label[..320];
                    wasCropped = true;
                }
            }
            if (!unrestrictedText && !c.IsInteractable && !c.IsTableContext && wordCount < minTextContextWords) continue;

            var dedupeName = label;
            if (wasCropped)
            {
                label += " [cropped]";
            }

            if (c.IsInteractable)
            {
                if (c.IsDropdown && !label.StartsWith("Dropdown:", StringComparison.OrdinalIgnoreCase))
                {
                    label = $"Dropdown: {label}";
                }
                else if (c.IsTextEntry && !label.StartsWith("TextInput:", StringComparison.OrdinalIgnoreCase))
                {
                    label = $"TextInput: {label}";
                }
            }

            var projected = new LabeledCandidate
            {
                Name = label,
                DedupeName = dedupeName,
                X = c.X,
                Y = c.Y,
                Left = c.Left,
                Top = c.Top,
                Right = c.Right,
                Bottom = c.Bottom,
                Width = c.Width,
                Height = c.Height,
                ViewportWidth = c.ViewportWidth,
                ViewportHeight = c.ViewportHeight,
                IsInteractable = c.IsInteractable
            };

            if (c.IsInteractable) interactables.Add(projected);
            else textOnly.Add(projected);
        }

        // C# structural filters:
        // 1) reject oversized containers
        // 2) reject larger parent containers when smaller clickable text candidates live inside them
        var filteredInteractables = new List<LabeledCandidate>(interactables.Count);
        foreach (var c in interactables.OrderBy(p => p.Area))
        {
            if (c.Width <= 0 || c.Height <= 0) continue;

            var viewportArea = (long)Math.Max(0, c.ViewportWidth) * Math.Max(0, c.ViewportHeight);
            if (viewportArea > 0 && c.Area > (long)(viewportArea * 0.45))
                continue;

            var shadowedBySmallerChild = false;
            foreach (var kept in filteredInteractables)
            {
                if (kept.Area >= c.Area) continue;
                if (kept.Name.Length < 4) continue;
                if (!ContainsPoint(c, kept.X, kept.Y)) continue;
                if (c.Area < kept.Area * 2) continue;

                if (c.Name.Contains(kept.Name, StringComparison.OrdinalIgnoreCase)
                    || c.Name.Equals(kept.Name, StringComparison.OrdinalIgnoreCase))
                {
                    shadowedBySmallerChild = true;
                    break;
                }
            }

            if (!shadowedBySmallerChild)
                filteredInteractables.Add(c);
        }

        // De-dupe in C#: same label + within 100px of another kept point.
        var result = new List<InteractableElement>(filteredInteractables.Count + textOnly.Count);
        var byLabel = new Dictionary<string, List<InteractableElement>>(StringComparer.OrdinalIgnoreCase);
        const int maxDistance = 100;
        const int maxDistanceSquared = maxDistance * maxDistance;

        foreach (var item in filteredInteractables)
        {
            var dedupeKey = NormalizeForDedupe(item.DedupeName);
            if (!byLabel.TryGetValue(dedupeKey, out var existing))
            {
                existing = new List<InteractableElement>();
                byLabel[dedupeKey] = existing;
            }

            var isNearDuplicate = false;
            foreach (var kept in existing)
            {
                var dx = item.X - kept.X;
                var dy = item.Y - kept.Y;
                if ((dx * dx) + (dy * dy) <= maxDistanceSquared)
                {
                    isNearDuplicate = true;
                    break;
                }
            }

            if (isNearDuplicate) continue;

            var resolved = new InteractableElement
            {
                Name = item.Name,
                X = item.X,
                Y = item.Y,
                IsInteractable = true,
                Left = item.Left,
                Top = item.Top,
                Right = item.Right,
                Bottom = item.Bottom
            };

            existing.Add(resolved);
            result.Add(resolved);
            if (result.Count >= maxElements) break;
        }

        // For non-interactable text: keep only largest containers, drop contained descendants.
        var filteredTextOnly = new List<LabeledCandidate>(textOnly.Count);
        foreach (var item in textOnly.OrderByDescending(t => t.Area))
        {
            if (item.Width <= 0 || item.Height <= 0) continue;
            var containedByKept = filteredTextOnly.Any(kept => ContainsRect(kept, item));
            if (containedByKept) continue;
            filteredTextOnly.Add(item);
        }

        // Add non-interactable visible text context.
        // No duplicates with interactables we already kept (same label + near point).
        foreach (var item in filteredTextOnly)
        {
            if (result.Count >= maxElements) break;
            var dedupeKey = NormalizeForDedupe(item.DedupeName);
            if (!byLabel.TryGetValue(dedupeKey, out var existing))
            {
                existing = new List<InteractableElement>();
                byLabel[dedupeKey] = existing;
            }

            // If this non-interactable text is already a subset of emitted text, omit it.
            if (dedupeKey.Length >= 4)
            {
                var isSubsetOfExisting = byLabel.Keys
                    .Any(k => !k.Equals(dedupeKey, StringComparison.OrdinalIgnoreCase)
                              && k.Contains(dedupeKey, StringComparison.OrdinalIgnoreCase));
                if (isSubsetOfExisting) continue;
            }

            // Also de-dupe repeated large text regardless of distance.
            if (byLabel.ContainsKey(dedupeKey) && byLabel[dedupeKey].Count > 0)
                continue;

            var isNearDuplicate = false;
            foreach (var kept in existing)
            {
                var dx = item.X - kept.X;
                var dy = item.Y - kept.Y;
                if ((dx * dx) + (dy * dy) <= maxDistanceSquared)
                {
                    isNearDuplicate = true;
                    break;
                }
            }

            if (isNearDuplicate) continue;

            var resolved = new InteractableElement
            {
                Name = item.Name,
                X = item.X,
                Y = item.Y,
                IsInteractable = false,
                Left = item.Left,
                Top = item.Top,
                Right = item.Right,
                Bottom = item.Bottom
            };

            existing.Add(resolved);
            result.Add(resolved);
        }

        return result;

        static bool ContainsPoint(LabeledCandidate box, int x, int y)
        {
            return x >= box.Left && x <= box.Right && y >= box.Top && y <= box.Bottom;
        }

        static bool ContainsRect(LabeledCandidate outer, LabeledCandidate inner)
        {
            return inner.Left >= outer.Left
                && inner.Top >= outer.Top
                && inner.Right <= outer.Right
                && inner.Bottom <= outer.Bottom;
        }

        static string NormalizeForDedupe(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            if (s.EndsWith(" [cropped]", StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^10].TrimEnd();
            }
            return s;
        }

        static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return "";
        }

        static int CountWords(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

    private sealed class LabeledCandidate
    {
        public string Name { get; set; } = "";
        public string DedupeName { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ViewportWidth { get; set; }
        public int ViewportHeight { get; set; }
        public bool IsInteractable { get; set; }
        public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
    }

    public static string ToSnapshotText(IReadOnlyList<InteractableElement>? items, int maxLines = 500)
    {
        if (items is null || items.Count == 0)
        {
            return "(no interactable elements from cursor/hover probe)";
        }

        var sb = new StringBuilder(items.Count * 48);
        var count = 0;
        var enforceCap = maxLines > 0;
        foreach (var it in items)
        {
            if (enforceCap && count >= maxLines)
            {
                sb.AppendLine("... (truncated)");
                break;
            }

            sb.Append(it.Name)
              .Append(" (P")
              .Append(it.X)
              .Append(',')
              .Append(it.Y)
              .Append(')');

            if (!it.IsInteractable)
            {
                sb.Append(" <R")
                  .Append(it.Left).Append(',')
                  .Append(it.Top).Append(',')
                  .Append(it.Right).Append(',')
                  .Append(it.Bottom).Append('>');
            }

            sb.AppendLine();
            count++;
        }

        return sb.ToString();
    }
}
