using PuppeteerSharp;
using PuppeteerSharp.Input;
using PuppeteerSharp.PageAccessibility;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using static BrowserCommandApp;
using static BrowserCommandApp.BrowserSession;
using static BrowserCommandApp.SnapshotFormatter;

var app = new BrowserCommandApp();
await app.RunAsync(args);

public sealed partial class BrowserCommandApp : IAsyncDisposable
{
	private const string DefaultProfileId = "default";
	private static readonly string BrowserPidPath = Path.Combine(AppContext.BaseDirectory, "cfagent_chrome_pids.txt");
	private static readonly JsonSerializerOptions CookieJarJson = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};
	private static readonly TextWriter StandardOutput = Console.Out;
	private static readonly AsyncLocal<TextWriter?> RoutedOutput = new();
	private static readonly AsyncLocal<Guid?> RoutedSessionId = new();
	private static int routedConsoleInstalled;

	private static void WriteDebugLine(string text)
	{
		Console.WriteLine(text);
		if (RoutedOutput.Value is not null)
		{
			StandardOutput.WriteLine(text);
		}
	}

	static string? GetString(JsonElement e, string prop)
	=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	private static void EnsureRoutedConsole()
	{
		if (Interlocked.Exchange(ref routedConsoleInstalled, 1) == 0)
		{
			Console.SetOut(new RoutedConsoleWriter());
		}
	}

	private static IDisposable PushCommandContext(Guid? sessionId, TextWriter? writer)
	{
		var priorSessionId = RoutedSessionId.Value;
		var priorWriter = RoutedOutput.Value;
		RoutedSessionId.Value = sessionId;
		RoutedOutput.Value = writer;
		return new CommandContextScope(priorSessionId, priorWriter);
	}

	private static string NormalizeProfileId(string? profileId)
	{
		var raw = string.IsNullOrWhiteSpace(profileId) ? DefaultProfileId : profileId.Trim();
		var sb = new System.Text.StringBuilder(raw.Length);
		foreach (var ch in raw)
		{
			sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
		}

		return sb.Length == 0 ? DefaultProfileId : sb.ToString();
	}

	private static string GetCookieJarPath(string? profileId)
		=> Path.Combine(AppContext.BaseDirectory, "profiles", NormalizeProfileId(profileId), "cookies.json");

	static bool IsBoringWrapper(AxRawFormatter.AxNode n)
	{
		if (!string.IsNullOrWhiteSpace(n.Name)) return false;
		return n.Role.Equals("none", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("generic", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("group", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("section", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("paragraph", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("presentation", StringComparison.OrdinalIgnoreCase);
	}
	internal static bool IsInteresting(SNode n)
	{
		if (!string.IsNullOrWhiteSpace(n.Name) || !string.IsNullOrWhiteSpace(n.ValueText) || !string.IsNullOrWhiteSpace(n.Description))
			return true;

		return n.Role.Equals("link", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("button", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("textbox", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("searchbox", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("radio", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("combobox", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
			|| n.Role.Equals("tab", StringComparison.OrdinalIgnoreCase);
	}


	private static string? ResolveUrlOrLinkCode(BrowserSession session, string arg)
	{
		arg = arg.Trim();
		if (arg.StartsWith("L", StringComparison.OrdinalIgnoreCase)
			&& session.LinkCodeToUrl.TryGetValue(arg, out var mapped))
		{
			return mapped;
		}
		return arg; // assume it's a URL
	}

	public static string ReplaceUrlsWithCodes(string text, BrowserSession session)
	{
		// SnapshotFormatter emits links as "<URL>https://..." so we can map them to stable codes.
		const string marker = "<URL>";
		var idx = 0;
		var sb = new System.Text.StringBuilder(text.Length);
		while (idx < text.Length)
		{
			var m = text.IndexOf(marker, idx, StringComparison.Ordinal);
			if (m < 0)
			{
				sb.Append(text, idx, text.Length - idx);
				break;
			}

			sb.Append(text, idx, m - idx);
			var urlStart = m + marker.Length;

			var urlEnd = urlStart;
			while (urlEnd < text.Length && !IsUrlTerminator(text[urlEnd]))
			{
				urlEnd++;
			}

			var url = text.Substring(urlStart, urlEnd - urlStart);
			var code = session.GetOrAddLinkCode(url);
			sb.Append(code);
			idx = urlEnd;
		}

		return sb.ToString();
	}

	private static bool IsUrlTerminator(char c)
	{
		// URLs in our snapshot rendering may be followed immediately by ')', ']', ',', etc.
		// Treat those as terminators so we don't accidentally swallow trailing punctuation.
		return char.IsWhiteSpace(c) || c is ')' or ']' or '}' or '>' or '"' or '\'' or ',' or ';';
	}

	public static class SnapshotFormatter
	{
		// LLM-friendly, low-noise rendering of the accessibility tree.
		// - omits null/default properties by construction
		// - flattens boring single-child wrapper nodes
		// - emits link targets as <URL>https://... which we post-process into codes (L1, L2, ...)
		public static string Format(SerializedAXNode? root, BrowserSession session)
		{
			if (root is null)
			{
				return "(no accessibility snapshot)";
			}

			var simplified = Simplify(root, session);
			SimplifyInPlace(simplified);

			var sb = new System.Text.StringBuilder(16 * 1024);
			RenderText(simplified, sb, indent: 0, session: session);
			return sb.ToString();
		}

		internal sealed class SNode
		{
			public string Role { get; init; } = string.Empty;
			public string? Name { get; set; }
			public string? ValueText { get; set; }
			public string? Description { get; set; }
			public string? Url { get; set; }
			public bool IsInteresting { get; set; }
			public List<SNode> Children { get; } = new();
		}

		private static SNode Simplify(SerializedAXNode node, BrowserSession session)
		{
			var s = new SNode
			{
				Role = node.Role ?? string.Empty,
				Name = Clean(node.Name),
				ValueText = Clean(node.ValueText),
				Description = Clean(node.Description),
				Url = ExtractUrl(node, session)
			};

			foreach (var child in node.Children ?? Array.Empty<SerializedAXNode>())
			{
				s.Children.Add(Simplify(child, session));
			}

			s.IsInteresting = IsInteresting(s);
			return s;
		}

		private static string? ExtractUrl(SerializedAXNode node, BrowserSession session)
		{
			// SerializedAXNode doesn't expose href/url directly in PuppeteerSharp.
			// We'll resolve link targets via a DOM crawl (see UpdateLinkTableFromDomAsync) using the node's Name.
			if (!string.Equals(node.Role, "link", StringComparison.OrdinalIgnoreCase))
				return null;

			var name = Clean(node.Name);
			if (string.IsNullOrWhiteSpace(name))
				return null;

			if (session.TryResolveUrlByLinkText(name, out var url))
			{
				session.GetOrAddLinkCode(url);
				return url;
			}

			// Fuzzy match: AX Name can differ slightly from DOM link text.
			if (session.TryResolveUrlByLinkTextFuzzy(name, out url))
			{
				session.GetOrAddLinkCode(url);
				return url;
			}

			return null;
		}

		private static string? Clean(string? s)
		{
			if (string.IsNullOrWhiteSpace(s)) return null;
			s = s.Trim();
			return s.Length == 0 ? null : s;
		}

		private static bool IsBoringWrapper(SNode n)
		{
			if (n.IsInteresting) return false;
			return n.Role.Equals("generic", StringComparison.OrdinalIgnoreCase)
				|| n.Role.Equals("group", StringComparison.OrdinalIgnoreCase)
				|| n.Role.Equals("section", StringComparison.OrdinalIgnoreCase)
				|| n.Role.Equals("paragraph", StringComparison.OrdinalIgnoreCase)
				|| n.Role.Equals("none", StringComparison.OrdinalIgnoreCase)
				|| n.Role.Equals("presentation", StringComparison.OrdinalIgnoreCase);
		}

		private static void SimplifyInPlace(SNode node)
		{
			for (int i = 0; i < node.Children.Count; i++)
			{
				SimplifyInPlace(node.Children[i]);
			}

			for (int i = 0; i < node.Children.Count; i++)
			{
				while (node.Children[i] is { } child && IsBoringWrapper(child) && child.Children.Count == 1)
				{
					var grand = child.Children[0];
					grand.Name = Merge(child.Name, grand.Name);
					grand.Description = Merge(child.Description, grand.Description);
					grand.ValueText = Merge(child.ValueText, grand.ValueText);
					node.Children[i] = grand;
				}
			}

			static string? Merge(string? a, string? b)
			{
				if (string.IsNullOrWhiteSpace(a)) return b;
				if (string.IsNullOrWhiteSpace(b)) return a;
				if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return b;
				return $"{a} {b}";
			}
		}

		private static void RenderText(SNode node, System.Text.StringBuilder sb, int indent, BrowserSession session)
		{
			static bool IsLandmarkRole(string role)
			{
				if (string.IsNullOrWhiteSpace(role)) return false;
				role = role.Trim();
				return role.Equals("navigation", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("main", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("banner", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("contentinfo", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("complementary", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("search", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("form", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("region", StringComparison.OrdinalIgnoreCase);
			}

			var printed = false;
			if (node.IsInteresting)
			{
				// Off-screen pruning: only print nodes we can resolve to a visible (viewport-intersecting) point.
				// Root-ish nodes have no reliable point, so allow those through.
				var text = node.Name ?? node.ValueText ?? node.Description;
				var isRootLike = node.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase)
					|| node.Role.Equals("document", StringComparison.OrdinalIgnoreCase);

				var hasOnScreenPoint = false;
				var hasClickablePoint = false;
				BrowserSession.ClickPoint ptClickable = default;

				if (!string.IsNullOrWhiteSpace(text))
				{
					hasOnScreenPoint = session.TryResolveVisiblePointByText(text, out _) || session.TryResolveVisiblePointByTextFuzzy(text, out _);
					hasClickablePoint = session.TryResolvePointByText(text, out ptClickable) || session.TryResolvePointByTextFuzzy(text, out ptClickable);
				}

				if (isRootLike || hasOnScreenPoint)
				{
					printed = true;
					if (indent > 0) sb.Append(' ', indent * 2);

					// SnapshotFormatter doesn't have stable node IDs or parent pointers like the raw CDP AX tree.
					// So we can only tag *self* if it is a landmark role.
					var landmark = IsLandmarkRole(node.Role) ? node.Role : null;
					if (!string.IsNullOrWhiteSpace(landmark))
					{
						sb.Append('[').Append(landmark).Append("] ");
					}

					sb.Append('[').Append(node.Role).Append("] ");
					sb.Append(!string.IsNullOrWhiteSpace(text) ? text : "(no text)");

					// Only append click points for things we believe are clickable.
					if (hasClickablePoint)
					{
						sb.Append(" (P").Append(ptClickable.X).Append(',').Append(ptClickable.Y).Append(')');
					}

					sb.AppendLine();
				}
			}

			var childIndent = printed ? indent + 1 : indent;
			foreach (var child in node.Children)
			{
				RenderText(child, sb, childIndent, session);
			}
		}
	}

	public static class AxRawFormatter
	{
		// A grouped rendering associates each on-screen AX line with the most specific scroll container
		// (smallest viewport-rect scrollable element containing the point). This helps the agent know
		// what region to scroll.
		public static string FormatGrouped(JsonElement root, int maxLines, BrowserSession session)
		{
			// First, render the usual pruned outline, but keep viewport points for grouping.
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("nodes", out var nodesEl) || nodesEl.ValueKind != JsonValueKind.Array)
				return "(raw AX: no nodes array)";

			var map = new Dictionary<string, AxNode>(StringComparer.Ordinal);
			foreach (var n in nodesEl.EnumerateArray())
			{
				var id = GetString(n, "nodeId");
				if (string.IsNullOrEmpty(id)) continue;
				map[id] = AxNode.FromJson(n);
			}
			if (map.Count == 0) return "(raw AX: empty)";

			var rootNode = map.Values.FirstOrDefault(x => x.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase))
				?? map.Values.First();

			// Build parent pointers so we can infer landmark/section roles (e.g., navigation) for descendants.
			var parentById = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var kvp in map)
			{
				var pid = kvp.Key;
				foreach (var cid in kvp.Value.ChildIds)
				{
					if (!parentById.ContainsKey(cid)) parentById[cid] = pid;
				}
			}

			// Pull current window scroll offsets once (needed to convert document coords -> viewport coords).
			var lines = new List<RenderedLine>(1024);
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var printedLines = 0;

			Render(rootNode.Id, indent: 0);

			if (lines.Count == 0)
				return "(raw AX: nothing interesting after pruning)";

			// Group lines by scroll container.
			var targets = session.ScrollTargets;
			var doc = targets.FirstOrDefault(t => t.Kind == "document");
			var targetOrder = targets
				.OrderBy(t => t.Kind == "document" ? 0 : 1)
				.ThenBy(t => t.Code, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			var groups = new Dictionary<string, List<RenderedLine>>(StringComparer.OrdinalIgnoreCase);
			foreach (var t in targetOrder) groups[t.Code] = new List<RenderedLine>();

			foreach (var ln in lines)
			{
				// RootWebArea lines often have no point; keep them under document.
				var code = doc?.Code ?? "S1";
				if (ln.Vx.HasValue && ln.Vy.HasValue)
				{
					code = session.ResolveScrollTargetForViewportPoint(ln.Vx.Value, ln.Vy.Value) ?? code;
				}
				if (!groups.TryGetValue(code, out var list))
				{
					list = new List<RenderedLine>();
					groups[code] = list;
				}
				list.Add(ln);
			}

			var sb = new System.Text.StringBuilder(32 * 1024);

			// Print grouped content
			foreach (var t in targetOrder)
			{
				if (!groups.TryGetValue(t.Code, out var list) || list.Count == 0) continue;

				sb.AppendLine($"-- {t.Code} {(t.Kind == "document" ? "document" : (t.Description ?? "element"))} {t.ScrollTop}/{t.MaxScrollTop} --");

				string? openLandmark = null;

				// Merge consecutive StaticText lines into one to reduce output size.
				string? staticIndent = null;
				var staticParts = new List<string>(8);

				void FlushStatic()
				{
					if (staticParts.Count == 0) return;
					var merged = string.Join(" ", staticParts.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
					if (merged.Length > 0)
					{
						sb.Append(staticIndent ?? string.Empty);
						sb.Append("[StaticText] ");
						sb.AppendLine(merged);
					}
					staticParts.Clear();
					staticIndent = null;
				}

				static bool TryParseStaticText(string line, out string indent, out string text)
				{
					indent = string.Empty;
					text = string.Empty;
					if (line is null) return false;
					var i = 0;
					while (i < line.Length && line[i] == ' ') i++;
					var trimmed = line.AsSpan(i);
					const string prefix = "[StaticText] ";
					if (!trimmed.StartsWith(prefix.AsSpan(), StringComparison.Ordinal)) return false;
					indent = i > 0 ? line[..i] : string.Empty;
					text = line.Substring(i + prefix.Length).Trim();
					return true;
				}

				foreach (var ln in list)
				{
					// Compress repeating landmark tags into blocks to save context.
					if (!string.Equals(openLandmark, ln.Landmark, StringComparison.OrdinalIgnoreCase))
					{
						FlushStatic();
						if (!string.IsNullOrWhiteSpace(openLandmark))
							sb.AppendLine($"[/{openLandmark}]");

						openLandmark = ln.Landmark;
						if (!string.IsNullOrWhiteSpace(openLandmark))
							sb.AppendLine($"[{openLandmark}]");
					}

					// Merge consecutive StaticText (same indent) into a single line.
					if (TryParseStaticText(ln.Text, out var indent, out var txt))
					{
						if (staticIndent is null) staticIndent = indent;
						if (!string.Equals(staticIndent, indent, StringComparison.Ordinal))
						{
							FlushStatic();
							staticIndent = indent;
						}
						if (!string.IsNullOrWhiteSpace(txt)) staticParts.Add(txt);
						continue;
					}

					FlushStatic();
					sb.AppendLine(ln.Text);
				}

				FlushStatic();

				if (!string.IsNullOrWhiteSpace(openLandmark))
					sb.AppendLine($"[/{openLandmark}]");
			}
			return sb.ToString();

			void Render(string id, int indent)
			{
				if (printedLines >= maxLines) return;
				if (!map.TryGetValue(id, out var node)) return;
				if (!seen.Add(id)) return;

				if (node.Ignored && !HasInterestingDescendant(id))
					return;

				id = Flatten(id);
				if (!map.TryGetValue(id, out node)) return;

				var printed = false;
				if (IsInterestingAx(node) && IsOnScreen(id, node, session))
				{
					printed = true;

					var sbLine = new System.Text.StringBuilder(256);
					if (indent > 0) sbLine.Append(' ', indent * 2);
					var landmark = GetLandmarkTag(id);
					sbLine.Append('[').Append(node.Role).Append("] ");
					var effectiveName = !string.IsNullOrWhiteSpace(node.Name) ? node.Name : GetEffectiveNameFromStaticText(id);
					var text = !string.IsNullOrWhiteSpace(effectiveName) ? effectiveName : "(no text)";
					sbLine.Append(text);

					// Click point (allow even when the AX node itself is StaticText/generic, as long as the DOM scan says this text is clickable)
					BrowserSession.ClickPoint pt= new BrowserSession.ClickPoint(0,0);
					var havePt =
						(!string.IsNullOrWhiteSpace(effectiveName)
							&& (session.TryResolvePointByText(effectiveName, out pt) || session.TryResolvePointByTextFuzzy(effectiveName, out pt)));

					if (!havePt && IsClickableRole(node.Role))
					{
						// Fallback: use AX bounds (page/document coords) when text lookup fails (common for inputs).
						if (node.BoundsX.HasValue && node.BoundsY.HasValue && node.BoundsW.HasValue && node.BoundsH.HasValue
							&& node.BoundsW.Value > 1 && node.BoundsH.Value > 1)
						{
							var cx = (int)Math.Round(node.BoundsX.Value + (node.BoundsW.Value / 2m));
							var cy = (int)Math.Round(node.BoundsY.Value + (node.BoundsH.Value / 2m));
							pt = new BrowserSession.ClickPoint(cx, cy);
							
						}
					}
					if (havePt)
						sbLine.Append(" (P").Append(pt.X).Append(',').Append(pt.Y).Append(')');

					static bool IsClickableRole(string role)
					{
						if (string.IsNullOrWhiteSpace(role)) return false;
						return role.Equals("link", StringComparison.OrdinalIgnoreCase)
							|| role.Equals("button", StringComparison.OrdinalIgnoreCase)
							|| role.Equals("textbox", StringComparison.OrdinalIgnoreCase)
							|| role.Equals("searchbox", StringComparison.OrdinalIgnoreCase)
							|| role.Equals("combobox", StringComparison.OrdinalIgnoreCase)
							|| role.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
							|| role.Equals("radio", StringComparison.OrdinalIgnoreCase)
							|| role.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
							|| role.Equals("tab", StringComparison.OrdinalIgnoreCase);
					}// Link URL as <URL>.. for later replacement
					if (node.Role.Equals("link", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(effectiveName))
					{
						if (session.TryResolveUrlByLinkText(effectiveName, out var url) || session.TryResolveUrlByLinkTextFuzzy(effectiveName, out url))
						{
							sbLine.Append(" <URL>").Append(url);
						}
					}

					if (node.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(node.Url))
					{
						sbLine.Append(" <URL>").Append(node.Url);
					}


					// Visible point for grouping (do NOT print)
					int? vx = null, vy = null;
					if (!string.IsNullOrWhiteSpace(effectiveName))
					{
						if (session.TryResolveVisiblePointByText(effectiveName, out pt) || session.TryResolveVisiblePointByTextFuzzy(effectiveName, out pt))
						{
							vx = pt.X - session.WindowScrollX;
							vy = pt.Y - session.WindowScrollY;
						}
					}

					lines.Add(new RenderedLine(sbLine.ToString(), vx, vy, string.IsNullOrWhiteSpace(landmark) ? null : landmark));
					printedLines++;
					indent++;
				}

				foreach (var c in node.ChildIds)
				{
					// Drop redundant StaticText children that simply repeat their interactive parent.
					// Example:
					//   [link] Navigation
					//     [StaticText] Navigation
					if (printed && IsRedundantStaticTextChild(node, c))
						continue;

					Render(c, printed ? indent : indent);
					if (printedLines >= maxLines) return;
				}
			}

			bool HasInterestingDescendant(string id)
			{
				var stack = new Stack<string>();
				var localSeen = new HashSet<string>(StringComparer.Ordinal);
				stack.Push(id);
				while (stack.Count > 0)
				{
					var cur = stack.Pop();
					if (!localSeen.Add(cur)) continue;
					if (!map.TryGetValue(cur, out var n)) continue;

					if (!n.Ignored && IsInterestingAx(n) && IsOnScreen(cur, n, session)) return true;
					foreach (var c in n.ChildIds) stack.Push(c);
				}
				return false;
			}

			bool IsRedundantStaticTextChild(AxNode parent, string childId)
			{
				if (!map.TryGetValue(childId, out var child)) return false;
				if (!child.Role.Equals("StaticText", StringComparison.OrdinalIgnoreCase)) return false;
				if (string.IsNullOrWhiteSpace(parent.Name) || string.IsNullOrWhiteSpace(child.Name)) return false;
				if (!parent.Name.Trim().Equals(child.Name.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

				// If the child has meaningful descendants, keep it.
				// Typically StaticText has InlineTextBox children; those are already suppressed.
				foreach (var gc in child.ChildIds)
				{
					if (!map.TryGetValue(gc, out var gcn)) continue;
					if (gcn.Role.Equals("InlineTextBox", StringComparison.OrdinalIgnoreCase)) continue;
					// Anything else suggests additional structure; keep it.
					return false;
				}

				// Only remove when the parent is an interactive/control role.
				return parent.Role.Equals("link", StringComparison.OrdinalIgnoreCase)
					|| parent.Role.Equals("button", StringComparison.OrdinalIgnoreCase)
					|| parent.Role.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
					|| parent.Role.Equals("tab", StringComparison.OrdinalIgnoreCase)
					|| parent.Role.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
					|| parent.Role.Equals("radio", StringComparison.OrdinalIgnoreCase)
					|| parent.Role.Equals("textbox", StringComparison.OrdinalIgnoreCase)
					|| parent.Role.Equals("searchbox", StringComparison.OrdinalIgnoreCase)
					|| parent.Role.Equals("combobox", StringComparison.OrdinalIgnoreCase);
			}

			string Flatten(string id)
			{
				var cur = id;
				for (int i = 0; i < 64; i++)
				{
					if (!map.TryGetValue(cur, out var n)) break;
					if (n.ChildIds.Count != 1) break;
					if (!BrowserCommandApp.IsBoringWrapper(n)) break;
					cur = n.ChildIds[0];
				}
				return cur;
			}

			bool IsOnScreen(string nodeId, AxNode n, BrowserSession session)
			{
				// RootWebArea doesn't map cleanly to a point; keep it so we retain URL context.
				if (n.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase))
					return true;

				var text = !string.IsNullOrWhiteSpace(n.Name) ? n.Name : GetEffectiveNameFromStaticText(nodeId);
				if (string.IsNullOrWhiteSpace(text))
					return false;

				return session.TryResolveVisiblePointByText(text, out _) || session.TryResolveVisiblePointByTextFuzzy(text, out _);
			}

			string? GetEffectiveNameFromStaticText(string nodeId)
			{
				// Accessibility trees often represent a clickable wrapper (e.g., link/button)
				// with an empty Name, while the visible label is a descendant StaticText.
				// We grab the first meaningful StaticText (or InlineTextBox) label we find.
				var stack = new Stack<(string id, int depth)>();
				var seenLocal = new HashSet<string>(StringComparer.Ordinal);
				stack.Push((nodeId, 0));
				while (stack.Count > 0)
				{
					var (cur, depth) = stack.Pop();
					if (depth > 12) continue;
					if (!seenLocal.Add(cur)) continue;
					if (!map.TryGetValue(cur, out var nn)) continue;

					if (nn.Role.Equals("StaticText", StringComparison.OrdinalIgnoreCase)
						|| nn.Role.Equals("InlineTextBox", StringComparison.OrdinalIgnoreCase))
					{
						if (!string.IsNullOrWhiteSpace(nn.Name))
							return nn.Name;
					}

					foreach (var cid in nn.ChildIds)
						stack.Push((cid, depth + 1));
				}
				return null;
			}

			static bool IsInterestingAx(AxNode n)
			{
				// Skip noisy InlineTextBox nodes entirely – they almost always duplicate StaticText.
				if (n.Role.Equals("InlineTextBox", StringComparison.OrdinalIgnoreCase))
					return false;

				// Be careful with icon placeholders like "?" — keep them only if they are interactive roles.
				if (string.Equals(n.Name, "?", StringComparison.Ordinal))
				{
					if (n.Role.Equals("button", StringComparison.OrdinalIgnoreCase)
						|| n.Role.Equals("link", StringComparison.OrdinalIgnoreCase)
						|| n.Role.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
						|| n.Role.Equals("tab", StringComparison.OrdinalIgnoreCase))
					{
						return true; // might be an icon button — keep it
					}
					// Non-interactive '?' is usually decorative noise.
					return false;
				}

				if (!string.IsNullOrWhiteSpace(n.Name)) return true;

				return n.Role.Equals("link", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("button", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("textbox", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("searchbox", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("radio", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("combobox", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("tab", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("heading", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("StaticText", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase);
			}

			string? GetLandmarkTag(string nodeId)
			{
				// Walk up ancestors (including self) looking for a landmark role.
				var cur = nodeId;
				for (var i = 0; i < 64 && !string.IsNullOrEmpty(cur); i++)
				{
					if (map.TryGetValue(cur, out var n))
					{
						var r = (n.Role ?? "").Trim();
						if (IsLandmarkRole(r)) return r;
					}
					if (!parentById.TryGetValue(cur, out var parent)) break;
					cur = parent;
				}
				return null;
			}

			static bool IsLandmarkRole(string role)
			{
				if (string.IsNullOrWhiteSpace(role)) return false;
				role = role.Trim();
				return role.Equals("navigation", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("main", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("banner", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("contentinfo", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("complementary", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("search", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("form", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("region", StringComparison.OrdinalIgnoreCase);
			}

			// (end helpers)
			static bool _dummy() => true;
		}

		private readonly record struct RenderedLine(string Text, int? Vx, int? Vy, string? Landmark);

		// Formats the raw CDP Accessibility.getFullAXTree output into an LLM-friendly outline.
		// Uses the session's DOM-derived maps to add actionable (P..,..) points and link URL codes when possible.
		public static string Format(JsonElement root, int maxLines, BrowserSession session)
		{
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("nodes", out var nodesEl) || nodesEl.ValueKind != JsonValueKind.Array)
				return "(raw AX: no nodes array)";

			var map = new Dictionary<string, AxNode>(StringComparer.Ordinal);
			foreach (var n in nodesEl.EnumerateArray())
			{
				var id = GetString(n, "nodeId");
				if (string.IsNullOrEmpty(id)) continue;
				map[id] = AxNode.FromJson(n);
			}

			if (map.Count == 0) return "(raw AX: empty)";

			var rootNode = map.Values.FirstOrDefault(x => x.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase))
				?? map.Values.First();

			// Build parent pointers so we can infer landmark/section roles (e.g., navigation) for descendants.
			var parentById = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var kvp in map)
			{
				var pid = kvp.Key;
				foreach (var cid in kvp.Value.ChildIds)
				{
					if (!parentById.ContainsKey(cid)) parentById[cid] = pid;
				}
			}

			var sb = new System.Text.StringBuilder(16 * 1024);
			var lines = 0;
			var seen = new HashSet<string>(StringComparer.Ordinal);

			Render(rootNode.Id, indent: 0);

			if (lines == 0) sb.Append("(raw AX: nothing interesting after pruning)");
			return sb.ToString();

			void Render(string id, int indent)
			{
				if (lines >= maxLines) return;
				if (!map.TryGetValue(id, out var node)) return;
				if (!seen.Add(id)) return;

				// If ignored and it doesn't lead to anything interesting, drop it.
				if (node.Ignored && !HasInterestingDescendant(id))
					return;

				// Flatten boring wrappers with a single child.
				id = Flatten(id);
				if (!map.TryGetValue(id, out node)) return;

				var printed = false;
				if (IsInteresting(node) && IsOnScreen(node, session))
				{
					printed = true;
					if (indent > 0) sb.Append(' ', indent * 2);

					sb.Append('[').Append(node.Role).Append("] ");
					var text = !string.IsNullOrWhiteSpace(node.Name) ? node.Name : "(no text)";
					sb.Append(text);

					// If we can resolve a click point from DOM text, append it.
					if (!string.IsNullOrWhiteSpace(node.Name))
					{
						if (session.TryResolvePointByText(node.Name, out var pt) || session.TryResolvePointByTextFuzzy(node.Name, out pt))
						{
							sb.Append(" (P").Append(pt.X).Append(',').Append(pt.Y).Append(')');
						}
					}

					// If it's a link and we can resolve a URL from DOM, emit it as <URL>... so the caller can map to Ln codes.
					if (node.Role.Equals("link", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(node.Name))
					{
						if (session.TryResolveUrlByLinkText(node.Name, out var url) || session.TryResolveUrlByLinkTextFuzzy(node.Name, out url))
						{
							sb.Append(" <URL>").Append(url);
						}
					}

					if (node.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(node.Url))
					{
						sb.Append(" <URL>").Append(node.Url);
					}

					sb.AppendLine();
					lines++;
					indent++;
				}

				foreach (var c in node.ChildIds)
				{
					Render(c, printed ? indent : indent);
					if (lines >= maxLines) return;
				}
			}

			static bool IsOnScreen(AxNode n, BrowserSession session)
			{
				// RootWebArea doesn't map cleanly to a point; keep it so we retain URL context.
				if (n.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase))
					return true;

				// Everything else: require a DOM-derived visible point.
				if (string.IsNullOrWhiteSpace(n.Name))
					return false;

				return session.TryResolveVisiblePointByText(n.Name, out _) || session.TryResolveVisiblePointByTextFuzzy(n.Name, out _);
			}

			string Flatten(string id)
			{
				var cur = id;
				for (int i = 0; i < 64; i++)
				{
					if (!map.TryGetValue(cur, out var n)) break;
					if (n.ChildIds.Count != 1) break;
					if (!IsBoringWrapper(n)) break;
					cur = n.ChildIds[0];
				}
				return cur;
			}

			bool HasInterestingDescendant(string id)
			{
				var stack = new Stack<string>();
				var localSeen = new HashSet<string>(StringComparer.Ordinal);
				stack.Push(id);
				while (stack.Count > 0)
				{
					var cur = stack.Pop();
					if (!localSeen.Add(cur)) continue;
					if (!map.TryGetValue(cur, out var n)) continue;

					if (!n.Ignored && IsInteresting(n) && IsOnScreen(n, session)) return true;
					foreach (var c in n.ChildIds) stack.Push(c);
				}
				return false;
			}

			static bool IsInteresting(AxNode n)
			{
				// Skip noisy InlineTextBox nodes entirely – they almost always duplicate StaticText.
				if (n.Role.Equals("InlineTextBox", StringComparison.OrdinalIgnoreCase))
					return false;

				// Be careful with icon placeholders like "?" — keep them only if they are interactive roles.
				if (string.Equals(n.Name, "?", StringComparison.Ordinal))
				{
					if (n.Role.Equals("button", StringComparison.OrdinalIgnoreCase)
						|| n.Role.Equals("link", StringComparison.OrdinalIgnoreCase)
						|| n.Role.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
						|| n.Role.Equals("tab", StringComparison.OrdinalIgnoreCase))
					{
						return true; // might be an icon button — keep it
					}
					// Non-interactive '?' is usually decorative noise.
					return false;
				}

				if (!string.IsNullOrWhiteSpace(n.Name)) return true;

				return n.Role.Equals("link", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("button", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("textbox", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("searchbox", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("radio", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("combobox", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("tab", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("heading", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("StaticText", StringComparison.OrdinalIgnoreCase)
					|| n.Role.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase);
			}

			string? GetLandmarkTag(string nodeId)
			{
				var cur = nodeId;
				for (var i = 0; i < 64 && !string.IsNullOrEmpty(cur); i++)
				{
					if (map.TryGetValue(cur, out var n))
					{
						var r = (n.Role ?? "").Trim();
						if (IsLandmarkRole(r)) return r;
					}
					if (!parentById.TryGetValue(cur, out var parent)) break;
					cur = parent;
				}
				return null;
			}

			static bool IsLandmarkRole(string role)
			{
				if (string.IsNullOrWhiteSpace(role)) return false;
				role = role.Trim();
				return role.Equals("navigation", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("main", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("banner", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("contentinfo", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("complementary", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("search", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("form", StringComparison.OrdinalIgnoreCase)
					|| role.Equals("region", StringComparison.OrdinalIgnoreCase);
			}

		}

		internal sealed class AxNode
		{
			public string Id { get; init; } = "";
			public bool Ignored { get; init; }
			public string Role { get; init; } = "";
			public string? Name { get; init; }
			public string? Url { get; init; }
			public List<string> ChildIds { get; init; } = new();
			public decimal? BoundsX { get; init; }
			public decimal? BoundsY { get; init; }
			public decimal? BoundsW { get; init; }
			public decimal? BoundsH { get; init; }
			public static AxNode FromJson(JsonElement n)
			{
				var role = "";
				if (n.TryGetProperty("role", out var r) && r.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.String)
					role = rv.GetString() ?? "";

				string? name = null;
				if (n.TryGetProperty("name", out var nm) && nm.TryGetProperty("value", out var nv) && nv.ValueKind == JsonValueKind.String)
					name = (nv.GetString() ?? "").Trim();
				if (string.IsNullOrWhiteSpace(name)) name = null;

				string? url = null;
				if (n.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
				{
					foreach (var p in props.EnumerateArray())
					{
						if (p.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String && pn.GetString() == "url")
						{
							if (p.TryGetProperty("value", out var pv) && pv.TryGetProperty("value", out var uv) && uv.ValueKind == JsonValueKind.String)
								url = uv.GetString();
						}
					}
				}
				if (string.IsNullOrWhiteSpace(url)) url = null;

				var kids = new List<string>();
				if (n.TryGetProperty("childIds", out var childIds) && childIds.ValueKind == JsonValueKind.Array)
				{
					foreach (var c in childIds.EnumerateArray())
						if (c.ValueKind == JsonValueKind.String) kids.Add(c.GetString()!);
				}
				decimal? bx = null, by = null, bw = null, bh = null;
				if (n.TryGetProperty("bounds", out var b) && b.ValueKind == JsonValueKind.Object)
				{
					if (b.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number) bx = x.GetDecimal();
					if (b.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number) by = y.GetDecimal();
					if (b.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) bw = w.GetDecimal();
					if (b.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) bh = h.GetDecimal();
				}

				return new AxNode
				{
					Id = GetString(n, "nodeId") ?? "",
					Ignored = n.TryGetProperty("ignored", out var ig) && ig.ValueKind == JsonValueKind.True,
					Role = role,
					Name = name,
					Url = url,
					ChildIds = kids,
					BoundsX = bx,
					BoundsY = by,
					BoundsW = bw,
					BoundsH = bh
				};
			}

			public static string? GetString(JsonElement e, string prop)
				=> e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
		}
	}

	private readonly ConcurrentDictionary<Guid, BrowserSession> sessions = new();
	private readonly BrowserLaunchConfig launchConfig = BrowserLaunchConfig.FromEnvironment();
	private readonly SemaphoreSlim browserInitGate = new(1, 1);
	private IBrowser? browser;
	private Guid? activeSessionId;
	private bool useAlternativeInteractableProbe = true;

	public async Task RunAsync(string[] args)
	{
		EnsureRoutedConsole();

		if (await TryRunWebServerAsync(args))
		{
			return;
		}

		Console.WriteLine("cfagentbrowser ready");

		// Safety: if we crashed last run, kill any recorded Chromium processes before starting.
		CleanupDanglingChromiumFromPidFile();

		// Convenience: start with a session immediately so you don't have to type `new-session`.
		await NewSessionAsync();

		while (true)
		{
			var line = Console.ReadLine();
			if (line is null)
			{
				break;
			}

			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			try
			{
				if (await HandleCommandAsync(line.Trim()))
				{
					break;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"error: {ex.Message}");
			}
		}

		await DisposeAsync();
	}

	private async Task<bool> HandleCommandAsync(string line)
	{
		var (command, args) = ParseCommand(line);

		switch (command)
		{
			case "new-session":
				await NewSessionAsync();
				break;
			case "use-session":
				UseSession(args);
				break;
			case "navigate":
				await NavigateAsync(args);
				break;
			case "get-snapshot":
				await GetSnapshotAsync(args);
				break;
			case "send-click":
				await SendClickAsync(args);
				break;
			case "send-click-focus":
				await SendClickAsync(args, ensureFocus: true);
				break;
			case "send-keys":
				await SendKeysAsync(args);
				break;
			case "send-enter":
				await SendEnterAsync();
				break;
			case "choose":
				await ChooseAsync(args);
				break;
			case "show-point":
				await ShowPointAsync(args);
				break;
			case "dump":
			case "raw-dump":
				await DumpAsync(args);
				break;
			case "full-text":
				await FullTextAsync(args);
				break;
			case "scroll-to":
				await ScrollToAsync(args);
				break;
			case "enter-text":
				await EnterTextAsync(args);
				break;
			case "back":
				await BackAsync();
				break;
			case "status":
				await StatusAsync(args);
				break;
			case "--alt":
				ToggleAlternativeProbe();
				break;
			case "quit":
				Console.WriteLine("bye");
				return true;
			default:
				Console.WriteLine($"error: unknown command '{command}'");
				break;
		}

		return false;
	}

	private static (string Command, string Args) ParseCommand(string line)
	{
		var firstSpace = line.IndexOf(' ');
		if (firstSpace < 0)
		{
			return (line.ToLowerInvariant(), string.Empty);
		}

		var command = line[..firstSpace].ToLowerInvariant();
		var args = line[(firstSpace + 1)..].Trim();
		return (command, args);
	}

	private void ToggleAlternativeProbe()
	{
		useAlternativeInteractableProbe = !useAlternativeInteractableProbe;
		Console.WriteLine(useAlternativeInteractableProbe
			? "ok: --alt enabled (using cursor/hover interactable probe for snapshots)"
			: "ok: --alt disabled (using accessibility snapshot path)");
	}

	// --- Crash-resilience: record Chromium PID(s) so we can clean up after crashes ---
	private static void CleanupDanglingChromiumFromPidFile()
	{
		try
		{
			if (!File.Exists(BrowserPidPath)) return;

			var lines = File.ReadAllLines(BrowserPidPath);
			foreach (var line in lines)
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				if (!int.TryParse(line.Trim(), out var pid)) continue;
				TryKillProcessTree(pid);
			}

			try { File.Delete(BrowserPidPath); } catch { }
		}
		catch
		{
			// Best-effort only.
		}
	}

	private static void TryPersistBrowserPid(IBrowser browser)
	{
		try
		{
			var pid = TryGetBrowserPid(browser);
			if (pid is null || pid <= 0) return;

			Directory.CreateDirectory(Path.GetDirectoryName(BrowserPidPath) ?? AppContext.BaseDirectory);
			File.WriteAllText(BrowserPidPath, pid.Value.ToString());
		}
		catch
		{
			// Best-effort only.
		}
	}

	private static void TryDeleteBrowserPidFile()
	{
		try { if (File.Exists(BrowserPidPath)) File.Delete(BrowserPidPath); } catch { }
	}

	private static int? TryGetBrowserPid(IBrowser browser)
	{
		try
		{
			var t = browser.GetType();

			// Common: Browser.Process (System.Diagnostics.Process)
			var procProp = t.GetProperty("Process");
			if (procProp?.GetValue(browser) is Process p)
			{
				try { return p.Id; } catch { }
			}

			// Sometimes: Browser.ProcessId
			var pidProp = t.GetProperty("ProcessId") ?? t.GetProperty("Pid");
			if (pidProp?.GetValue(browser) is int pid && pid > 0) return pid;
			if (pidProp?.GetValue(browser) is long pidL && pidL > 0) return (int)pidL;
		}
		catch { }

		return null;
	}

	private static void TryKillProcessTree(int pid)
	{
		try
		{
			if (pid <= 0) return;

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				// /T kills child processes; /F forces.
				try
				{
					Process.Start(new ProcessStartInfo
					{
						FileName = "taskkill",
						Arguments = $"/PID {pid} /T /F",
						CreateNoWindow = true,
						UseShellExecute = false
					});
				}
				catch { }
				return;
			}

			// Cross-platform best effort.
			Process? proc = null;
			try { proc = Process.GetProcessById(pid); } catch { proc = null; }
			if (proc is null) return;

			// Prefer Kill(entireProcessTree: true) where available.
			var killTree = typeof(Process).GetMethod("Kill", new[] { typeof(bool) });
			if (killTree is not null)
			{
				try { killTree.Invoke(proc, new object[] { true }); return; } catch { }
			}

			try { proc.Kill(); } catch { }
		}
		catch
		{
			// Best-effort only.
		}
	}
	// --- end crash-resilience helpers ---

	private async Task EnsureBrowserAsync()
	{
		if (browser is not null)
		{
			return;
		}

		await browserInitGate.WaitAsync();
		try
		{
			if (browser is not null)
			{
				return;
			}

			// Extra safety: ensure we don't start a second Chromium if an old one is still recorded.
			CleanupDanglingChromiumFromPidFile();

			var chromiumPath = ResolveChromiumPath(launchConfig);

			if (chromiumPath is null && launchConfig.AllowDownloadChromium)
			{
				Console.WriteLine("Downloading compatible Chromium (first run)…");
				var fetcher = new BrowserFetcher();
				await fetcher.DownloadAsync();
			}

			var launchArgs = new List<string>
			{
				"--no-sandbox",
				"--disable-gpu",
				"--disable-blink-features=AutomationControlled",
				"--font-render-hinting=none",
				"--hide-scrollbars"
			};

			if (launchConfig.ExtraChromiumArgs.Count > 0)
			{
				launchArgs.AddRange(launchConfig.ExtraChromiumArgs);
			}

			var launchOptions = new LaunchOptions
			{
				Headless = true,
				ExecutablePath = chromiumPath,
				Args = launchArgs.ToArray(),
				Timeout = launchConfig.LaunchTimeoutMs,

#pragma warning disable CS0618 // ProtocolTimeout is marked obsolete in some PuppeteerSharp versions; still set it for versions where it is honored.
				ProtocolTimeout = launchConfig.ProtocolTimeoutMs
#pragma warning restore CS0618
			};

			using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(launchConfig.LaunchTimeoutMs));
			browser = await Puppeteer.LaunchAsync(launchOptions).WaitAsync(cts.Token);
			TryPersistBrowserPid(browser);
		}
		finally
		{
			browserInitGate.Release();
		}
	}

	private static string? ResolveChromiumPath(BrowserLaunchConfig cfg)
	{
		// Prefer an explicit path if provided.
		string? chromiumPath = cfg switch
		{
			{ } when !string.IsNullOrWhiteSpace(cfg.ChromiumPath) && File.Exists(cfg.ChromiumPath) => cfg.ChromiumPath,
			_ => null
		};

		if (chromiumPath is null)
		{
			IEnumerable<string> candidates = Array.Empty<string>();

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				candidates =
				[
					"/usr/bin/chromium",
					"/usr/bin/chromium-browser",
					"/usr/bin/google-chrome",
					"/usr/bin/chrome"
				];
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\\Program Files";
				var pfx86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\\Program Files (x86)";
				var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

				candidates =
				[
					Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
					Path.Combine(pfx86, "Google", "Chrome", "Application", "chrome.exe"),
					Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
					Path.Combine(pfx86, "Microsoft", "Edge", "Application", "msedge.exe"),
					Path.Combine(lad, "Chromium", "Application", "chrome.exe")
				];
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			{
				candidates =
				[
					"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
					"/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
					"/Applications/Chromium.app/Contents/MacOS/Chromium"
				];
			}

			foreach (var candidate in candidates)
			{
				if (File.Exists(candidate))
				{
					chromiumPath = candidate;
					break;
				}
			}

			if (chromiumPath is null)
			{
				var env = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH")
						  ?? Environment.GetEnvironmentVariable("CHROME_PATH");

				if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
				{
					chromiumPath = env;
				}
			}
		}

		return chromiumPath;
	}

	private async Task<IPage> NewPageAsync(IBrowserContext context)
	{
		var page = await context.NewPageAsync();

		// Timeouts: make sure we never inherit some accidental 1s default.
		page.DefaultTimeout = launchConfig.DefaultTimeoutMs;
		page.DefaultNavigationTimeout = launchConfig.NavigationTimeoutMs;

		// Debug hooks to catch "my page became null" moments.
		page.Close += (_, _) => Console.WriteLine("debug: page closed");
		page.Error += (_, e) => Console.WriteLine("debug: page error: " + e);

		// Light anti-detection shim for common checks.
		await page.EvaluateExpressionOnNewDocumentAsync("Object.defineProperty(navigator, 'webdriver', { get: () => false });");

		await page.SetUserAgentAsync(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
			"AppleWebKit/537.36 (KHTML, like Gecko) " +
			"Chrome/122.0.0.0 Safari/537.36");

		await page.SetViewportAsync(new ViewPortOptions
		{
			Width = 1152,
			Height = 2048,
			DeviceScaleFactor = 1,
			IsMobile = false
		});

		// Some sites decide "mobile" via media queries like (max-device-width: 992px),
		// which depend on emulated device/screen metrics (not just innerWidth).
		// Force desktop-like device metrics so matchMedia('(max-device-width: ...)') behaves correctly.
		var cdp = await page.Target.CreateCDPSessionAsync();
		await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new
		{
			width = 1152,
			height = 2048,
			deviceScaleFactor = 1,
			mobile = false,
			screenWidth = 1152,
			screenHeight = 2048
		});

		await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
		{
			["Accept-Language"] = "en-US,en;q=0.9"
		});

		return page;
	}

	private async Task NewSessionAsync()
	{
		await EnsureBrowserAsync();

		var sessionId = Guid.NewGuid();
		var profileId = DefaultProfileId;
		var context = await browser!.CreateBrowserContextAsync();
		var page = await NewPageAsync(context);

		sessions[sessionId] = new BrowserSession(sessionId, profileId, (BrowserContext)context, page);
		activeSessionId = sessionId;

		// Restore cookies/local storage for the new context.
		try
		{
			await BrowserSession.RestoreCookieJarAsync((BrowserContext)context, profileId);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"warn: cookie restore failed ({ex.GetType().Name}: {ex.Message})");
		}

		Console.WriteLine(sessionId);
	}

	private void UseSession(string args)
	{
		if (!Guid.TryParse(args, out var sessionId))
		{
			Console.WriteLine("error: use-session requires a valid GUID");
			return;
		}

		if (!sessions.ContainsKey(sessionId))
		{
			Console.WriteLine($"error: session '{sessionId}' not found");
			return;
		}

		activeSessionId = sessionId;
		Console.WriteLine($"using {sessionId}");
	}

	private BrowserSession? TryGetActiveSession()
	{
		var sessionId = RoutedSessionId.Value ?? activeSessionId;
		if (sessionId is null)
		{
			Console.WriteLine("error: no active session. create one with new-session first");
			return null;
		}

		if (!sessions.TryGetValue(sessionId.Value, out var session))
		{
			Console.WriteLine("error: active session does not exist");
			if (RoutedSessionId.Value is null)
			{
				activeSessionId = null;
			}
			return null;
		}

		return session;
	}

	private async Task NavigateAsync(string args)
	{
		if (string.IsNullOrWhiteSpace(args))
		{
			Console.WriteLine("error: navigate requires a URL or link code (e.g., L15)");
			return;
		}

		var session = TryGetActiveSession();
		if (session is null)
		{
			return;
		}

		var url = ResolveUrlOrLinkCode(session, args);
		if (url is null)
		{
			Console.WriteLine($"error: unknown link code '{args.Trim()}'");
			return;
		}

		var beforeUrl = SafeGetUrl(session.Page);
		for (var attempt = 1; attempt <= launchConfig.NavigateMaxAttempts; attempt++)
		{
			try
			{
				// Occasionally PuppeteerSharp pages get into a "half-alive" state where IsClosed is false
				// but the main frame (and Url getter) throws. Treat that as unhealthy and recreate.
				if (!IsPageHealthy(session.Page))
				{
					Console.WriteLine("warn: page appears unhealthy; recreating before navigate");
					await session.ReplacePageAsync(NewPageAsync);
				}

				if (session.Page.IsClosed)
				{
					Console.WriteLine("debug: page was closed; creating a new page");
					await session.ReplacePageAsync(NewPageAsync);
				}

				await session.Page.GoToAsync(url, new NavigationOptions
				{
					WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
					Timeout = launchConfig.NavigationTimeoutMs
				});

				session.RecordHistory(beforeUrl, SafeGetUrl(session.Page));

				// Let any late AJAX/layout work settle a bit after DOMContentLoaded.
				await Task.Delay(2000);

				await DumpSnapshotAsync(session);

				// Persist cookies after navigations (logins often happen here).
				await BrowserSession.PersistCookieJarAsync(session.Context, session.ProfileId);
				return;
			}
			catch (Exception ex) when (attempt < launchConfig.NavigateMaxAttempts && IsRecoverableNavigateFailure(ex))
			{
				Console.WriteLine($"warn: navigate failure (attempt {attempt}/{launchConfig.NavigateMaxAttempts}): {ex.GetType().Name}: {ex.Message}. Recreating page and retrying…");
				await session.ReplacePageAsync(NewPageAsync);
				continue;
			}
		}

		static bool IsRecoverableNavigateFailure(Exception ex)
		{
			// 1) Known flaky CDP attach / frame detach cases
			if (ex is NavigationException nex)
			{
				if (nex.Message.Contains("Target.attachedToTarget", StringComparison.OrdinalIgnoreCase)
					|| nex.Message.Contains("responseReceivedExtraInfo", StringComparison.OrdinalIgnoreCase)
					|| nex.Message.Contains("Navigating frame was detached", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			// 2) PuppeteerSharp sometimes throws a raw NullReferenceException when the Page/MainFrame is gone
			// but IsClosed hasn't updated yet (Url getter can also throw).
			if (ex is NullReferenceException) return true;
			if (ex.Message.Contains("Object reference not set", StringComparison.OrdinalIgnoreCase)) return true;

			// 3) Target closed / disconnected cases often benefit from recreating.
			if (ex is TargetClosedException) return true;
			if (ex is PuppeteerException pex && pex.Message.Contains("Session closed", StringComparison.OrdinalIgnoreCase)) return true;

			return false;
		}

	}

	private async Task BackAsync()
	{
		var session = TryGetActiveSession();
		if (session is null)
			return;

		if (!IsPageHealthy(session.Page))
		{
			Console.WriteLine("warn: page appears unhealthy before back; recreating");
			await session.ReplacePageAsync(NewPageAsync);
		}

		if (!session.TryPopHistory(out var targetUrl))
		{
			Console.WriteLine("error: no previous page in history");
			return;
		}

		try
		{
			await session.Page.GoToAsync(targetUrl, new NavigationOptions
			{
				WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
				Timeout = launchConfig.NavigationTimeoutMs
			});

			await Task.Delay(1000);
			await DumpSnapshotAsync(session);
			try { await BrowserSession.PersistCookieJarAsync(session.Context, session.ProfileId); } catch { }
		}
		catch (Exception ex)
		{
			Console.WriteLine($"error: back failed ({ex.GetType().Name}: {ex.Message})");
		}
	}
	static bool IsPageHealthy(IPage page)
	{
		try
		{
			if (page.IsClosed) return false;
			if (page.MainFrame == null) return false;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private async Task StatusAsync(string args)
	{
		// Keep status current when running in HTTP mode.
		await CleanupIdleSessionsAsync(TimeSpan.FromMinutes(10));

		Console.WriteLine("STATUS");
		Console.WriteLine($"sessions: {sessions.Count}");
		Console.WriteLine($"active-session: {(activeSessionId.HasValue ? activeSessionId.Value.ToString() : "none")}");

		var nowUtc = DateTime.UtcNow;
		foreach (var s in sessions.Values.OrderBy(v => v.Id))
		{
			var idleSec = Math.Max(0, (int)(nowUtc - s.LastTouchedUtc).TotalSeconds);
			Console.WriteLine($"session {s.Id} profile={s.ProfileId} idle={idleSec}s");
		}

		var trackedPids = GetTrackedBrowserPids().OrderBy(v => v).ToArray();
		if (trackedPids.Length == 0)
		{
			Console.WriteLine("browser-pids: none");
			Console.WriteLine("browser-memory-mb: 0");
			return;
		}

		Console.WriteLine("browser-pids: " + string.Join(",", trackedPids));

		long totalWorkingSet = 0;
		var details = new List<string>(trackedPids.Length);
		foreach (var pid in trackedPids)
		{
			try
			{
				using var p = Process.GetProcessById(pid);
				var ws = p.WorkingSet64;
				totalWorkingSet += Math.Max(0, ws);
				details.Add($"{pid}={ws / (1024 * 1024)}MB");
			}
			catch
			{
				details.Add($"{pid}=unavailable");
			}
		}

		Console.WriteLine("browser-memory-detail: " + string.Join(" | ", details));
		Console.WriteLine($"browser-memory-mb: {totalWorkingSet / (1024 * 1024)}");
	}

	private HashSet<int> GetTrackedBrowserPids()
	{
		var set = new HashSet<int>();
		try
		{
			if (browser is not null)
			{
				var livePid = TryGetBrowserPid(browser);
				if (livePid is > 0) set.Add(livePid.Value);
			}
		}
		catch { }

		try
		{
			if (File.Exists(BrowserPidPath))
			{
				foreach (var line in File.ReadAllLines(BrowserPidPath))
				{
					if (int.TryParse(line?.Trim(), out var pid) && pid > 0)
					{
						set.Add(pid);
					}
				}
			}
		}
		catch { }

		return set;
	}
	private async Task GetSnapshotAsync(string args)
	{
		// Args optional:
		// - [width] [height]
		// - [path]
		// - [width] [height] [path]
		var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		var session = TryGetActiveSession();
		if (session is null)
			return;

		int width;
		int height;

		// If no dimensions supplied, reuse current viewport (or defaults).
		if (parts.Length >= 2
			&& int.TryParse(parts[0], out width)
			&& int.TryParse(parts[1], out height))
		{
			await EnsureViewportAsync(session, width, height);
		}
		else
		{
			width = session.ViewportWidth ?? 1152;
			height = session.ViewportHeight ?? 2048;
		}

		// Optional output folder path.
		// If width/height are provided, the path is token 3+; otherwise treat all args as the path.
		var outputFolder = string.Empty;
		if (parts.Length >= 2
			&& int.TryParse(parts[0], out _)
			&& int.TryParse(parts[1], out _))
		{
			outputFolder = parts.Length >= 3 ? string.Join(' ', parts.Skip(2)) : string.Empty;
		}
		else if (!string.IsNullOrWhiteSpace(args))
		{
			outputFolder = args.Trim();
		}

		if ((outputFolder.StartsWith('"') && outputFolder.EndsWith('"'))
			|| (outputFolder.StartsWith('\'') && outputFolder.EndsWith('\'')))
		{
			outputFolder = outputFolder[1..^1].Trim();
		}

		string outputPath;
		var isTemp = false;
		if (string.IsNullOrWhiteSpace(outputFolder))
		{
			var name = $"cfagent_snapshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jpg";
			outputPath = Path.Combine(Path.GetTempPath(), name);
			isTemp = true;
		}
		else
		{
			Directory.CreateDirectory(outputFolder);
			outputPath = Path.Combine(outputFolder, "File.dat");
		}

		// Higher quality snapshot: temporarily increase DPR to 2.
		// Some pages can transiently report innerWidth/innerHeight=0; never let that poison snapshot dimensions.
		var vp = await session.Page.EvaluateFunctionAsync<ViewportPoint>("() => ({ Vx: window.innerWidth|0, Vy: window.innerHeight|0, Vw: window.innerWidth|0, Vh: window.innerHeight|0 })");
		var snapshotViewW = vp.Vw > 0 ? vp.Vw : (width > 0 ? width : (session.ViewportWidth.GetValueOrDefault() > 0 ? session.ViewportWidth!.Value : 1152));
		var snapshotViewH = vp.Vh > 0 ? vp.Vh : (height > 0 ? height : (session.ViewportHeight.GetValueOrDefault() > 0 ? session.ViewportHeight!.Value : 2048));

		await session.Page.SetViewportAsync(new ViewPortOptions
		{
			Width = snapshotViewW,
			Height = snapshotViewH,
			DeviceScaleFactor = 2,
			IsMobile = false
		});

		await session.Page.ScreenshotAsync(outputPath, new ScreenshotOptions
		{
			Type = ScreenshotType.Jpeg,
			FullPage = false,
			Quality = 90
		});

		session.LastSnapshotImageWidth = Math.Max(1, snapshotViewW * 2);
		session.LastSnapshotImageHeight = Math.Max(1, snapshotViewH * 2);

		// Restore DPR=1 so click coordinates remain correct
		await session.Page.SetViewportAsync(new ViewPortOptions
		{
			Width = snapshotViewW,
			Height = snapshotViewH,
			DeviceScaleFactor = 1,
			IsMobile = false
		});

		Console.WriteLine($"URL: {SafeGetUrl(session.Page)}");
		Console.WriteLine(ToFileUri(outputPath));

		if (isTemp)
		{
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(TimeSpan.FromMinutes(1));
					try { File.Delete(outputPath); } catch { }
				}
				catch { }
			});
		}
	}

	private async Task DumpAsync(string args)
	{
		var session = TryGetActiveSession();
		if (session is null)
			return;

		// Keep consistent with other commands.
		if (!IsPageHealthy(session.Page))
		{
			Console.WriteLine("warn: page appears unhealthy before dump; recreating");
			await session.ReplacePageAsync(NewPageAsync);
		}

		Console.WriteLine($"URL: {SafeGetUrl(session.Page)}");

		// Include scrollable containers and pseudo-scroll links in dump output too.
		await PrintScrollablesAsync(session, includeLinks: true);

		// Dump should also use the alt pipeline.
		if (await TryDumpAlternativeSnapshotAsync(session, maxLinesPerGroup: 800, maxElements: 1200, unrestrictedText: false, compactOutput: false))
			return;

		try
		{
			var client = await session.Page.Target.CreateCDPSessionAsync();

			// This is the raw CDP accessibility tree; structure varies across Chromium versions.
			var result = await client.SendAsync("Accessibility.getFullAXTree");

			// Prune the raw AX tree down to an LLM-friendly text outline.
			var elem = result is JsonElement je ? je : JsonSerializer.SerializeToElement(result);
			// Prefer up-to-date scroll targets first (used to assign duplicate text items to the right region)
			await session.UpdateScrollTargetsFromDomAsync();
			await session.UpdateLinkTableFromDomAsync();
			var pruned = AxRawFormatter.FormatGrouped(elem, maxLines: 800, session: session);
			pruned = ReplaceUrlsWithCodes(pruned, session);
			Console.WriteLine(pruned);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"error: dump failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private async Task FullTextAsync(string args)
	{
		var session = TryGetActiveSession();
		if (session is null)
			return;

		if (!IsPageHealthy(session.Page))
		{
			Console.WriteLine("warn: page appears unhealthy before full-text; recreating");
			await session.ReplacePageAsync(NewPageAsync);
		}

		await session.UpdateScrollTargetsFromDomAsync();
		await session.UpdateLinkTableFromDomAsync();

		Console.WriteLine($"URL: {SafeGetUrl(session.Page)}");
		await PrintScrollablesAsync(session, includeLinks: true);

		if (!await TryDumpAlternativeSnapshotAsync(session, maxLinesPerGroup: 0, maxElements: 12000, unrestrictedText: true, compactOutput: false))
		{
			Console.WriteLine("error: full-text failed");
		}
	}

	private async Task ScrollToAsync(string args)
	{
		// Usage:
		//   scroll-to S2 up
		//   scroll-to S2 down
		//   scroll-to S2 -1
		//   scroll-to S2 1
		if (string.IsNullOrWhiteSpace(args))
		{
			Console.WriteLine("error: scroll-to requires a code like S2 and a direction (up/down or -1/1)");
			return;
		}

		var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2)
		{
			Console.WriteLine("error: scroll-to requires a code like S2 and a direction (up/down or -1/1)");
			return;
		}

		var code = parts[0].Trim();
		var dirToken = parts[1].Trim();
		var dir = 0;
		if (dirToken.Equals("down", StringComparison.OrdinalIgnoreCase) || dirToken == "1" || dirToken == "+1") dir = 1;
		else if (dirToken.Equals("up", StringComparison.OrdinalIgnoreCase) || dirToken == "-1") dir = -1;
		else
		{
			Console.WriteLine("error: scroll-to direction must be up/down or -1/1");
			return;
		}

		var session = TryGetActiveSession();
		if (session is null)
			return;

		if (!IsPageHealthy(session.Page))
		{
			Console.WriteLine("warn: page appears unhealthy before scroll-to; recreating");
			await session.ReplacePageAsync(NewPageAsync);
		}

		// Refresh targets so S-codes are current.
		await session.UpdateScrollTargetsFromDomAsync();
		if (!session.ScrollCodeToTarget.TryGetValue(code, out var target))
		{
			Console.WriteLine($"error: unknown scroll target '{code}'");
			return;
		}

		try
		{
			var res = await session.Page.EvaluateFunctionAsync<ScrollResult>(@"(t, dir) => {
				const clamp = (v, a, b) => Math.min(Math.max(v, a), b);
				const vw = Math.max(0, window.innerWidth || 0);
				const vh = Math.max(0, window.innerHeight || 0);

				const isScrollableY = (el) => {
					try {
						if (!el || el.nodeType !== 1) return false;
						const cs = window.getComputedStyle(el);
						if (!cs) return false;
						const oy = cs.overflowY;
						if (!(oy === 'auto' || oy === 'scroll' || oy === 'overlay')) return false;
						if ((el.clientHeight|0) <= 0) return false;
						if ((el.scrollHeight|0) <= (el.clientHeight|0) + 5) return false;
						return true;
					} catch { return false; }
				};

				const pickScrollElFromPoint = (x, y, hint) => {
					// Prefer the closest scrollable ancestor of the hit element.
					let hit = null;
					try { hit = document.elementFromPoint(x, y); } catch { hit = null; }
					let cur = hit;
					let best = null;
					let bestScore = 1e18;

					for (let steps = 0; cur && steps < 30; steps++) {
						if (isScrollableY(cur)) {
							let score = 0;
							try {
								const r = cur.getBoundingClientRect();
								// Score by how well it matches the stored rect (roughly).
								const dx = Math.abs((r.left|0) - (hint.x|0));
								const dy = Math.abs((r.top|0) - (hint.y|0));
								const dw = Math.abs((r.width|0) - (hint.w|0));
								const dh = Math.abs((r.height|0) - (hint.h|0));
								score = dx + dy + dw + dh;
							} catch { score = 1e9; }
							if (score < bestScore) { bestScore = score; best = cur; }
						}
						cur = cur.parentElement;
					}

					// If no ancestor matched, try a broader scan near the point.
					if (!best) {
						const els = document.elementsFromPoint ? document.elementsFromPoint(x, y) : (hit ? [hit] : []);
						for (const el of els) {
							let c = el;
							for (let steps = 0; c && steps < 30; steps++) {
								if (isScrollableY(c)) {
									best = c;
									break;
								}
								c = c.parentElement;
							}
							if (best) break;
						}
					}

					return best;
				};

				const doScrollDocument = () => {
					const se = document.scrollingElement || document.documentElement;
					const before = Math.round(se.scrollTop || window.scrollY || 0);
					const sh = Math.round(se.scrollHeight || document.documentElement.scrollHeight || 0);
					const ch = Math.round(se.clientHeight || window.innerHeight || 0);
					const max = Math.max(0, sh - ch);
					const delta = Math.max(32, ch - 32);
					const after = clamp(before + dir * delta, 0, max);
					se.scrollTop = after;
					return { before, after, max };
				};

				const doScrollElement = (hint) => {
					// Pick a point inside the container rect (viewport coords) as the anchor.
					const ax = clamp(Math.round((hint.x|0) + (hint.w|0) / 2), 1, Math.max(1, vw - 2));
					const ay = clamp(Math.round((hint.y|0) + (hint.h|0) / 2), 1, Math.max(1, vh - 2));
					const el = pickScrollElFromPoint(ax, ay, hint);
					if (!el) return { before: 0, after: 0, max: 0, error: 'no scrollable element found at target point' };
					const before = Math.round(el.scrollTop || 0);
					const sh = Math.round(el.scrollHeight || 0);
					const ch = Math.round(el.clientHeight || 0);
					const max = Math.max(0, sh - ch);
					const delta = Math.max(32, ch - 32);
					const after = clamp(before + dir * delta, 0, max);
					el.scrollTop = after;
					return { before, after, max };
				};

				try {
					if ((t.kind || '').toLowerCase() === 'document') {
						const r = doScrollDocument();
						return { ok: true, before: r.before, after: r.after, max: r.max, code: t.code || '' };
					}

					const r = doScrollElement({ x: t.x|0, y: t.y|0, w: t.w|0, h: t.h|0 });
					if (r.error) return { ok: false, before: 0, after: 0, max: 0, code: t.code || '', error: r.error };
					return { ok: true, before: r.before, after: r.after, max: r.max, code: t.code || '' };
				} catch (e) {
					return { ok: false, before: 0, after: 0, max: 0, code: t.code || '', error: String(e && e.message ? e.message : e) };
				}
			}", new
			{
				code = target.Code,
				kind = target.Kind,
				x = target.X,
				y = target.Y,
				w = target.W,
				h = target.H
			}, dir);

			if (!res.Ok)
			{
				Console.WriteLine($"error: scroll-to failed for {code}: {res.Error}");
				return;
			}

			Console.WriteLine($"ok {code} {res.Before}/{res.Max} -> {res.After}/{res.Max}");
			await DumpSnapshotAsync(session);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"error: scroll-to failed ({ex.GetType().Name}: {ex.Message})");
		}
	}

	private sealed class ScrollResult
	{
		public bool Ok { get; set; }
		public string Code { get; set; } = "";
		public int Before { get; set; }
		public int After { get; set; }
		public int Max { get; set; }
		public string? Error { get; set; }
	}

	private sealed class DropdownClickResult
	{
		public bool IsSelect { get; set; }
		public string Field { get; set; } = "";
		public string Current { get; set; } = "";
		public string[] Options { get; set; } = Array.Empty<string>();
	}

	private sealed class ChooseResult
	{
		public bool Ok { get; set; }
		public string Error { get; set; } = "";
		public string Current { get; set; } = "";
	}

	private async Task SendClickAsync(string args, bool ensureFocus = false)
	{
		if (string.IsNullOrWhiteSpace(args))
		{
			Console.WriteLine("error: send-click requires a point like P150,120");
			return;
		}

		var session = TryGetActiveSession();
		if (session is null)
		{
			return;
		}

		var rawPointArg = args.Trim();
		var isPrefixedPoint = IsPrefixedPoint(rawPointArg);
		if (!TryParsePoint(rawPointArg, out var x, out var y))
		{
			Console.WriteLine("error: send-click requires a point like P150,120");
			return;
		}

		// Same "page health" guard as navigate: clicks can occur after a detached target / recycled page.
		if (!IsPageHealthy(session.Page))
		{
			Console.WriteLine("warn: page appears unhealthy before click; recreating");
			await session.ReplacePageAsync(NewPageAsync);
		}

		try
		{
			var beforeUrl = SafeGetUrl(session.Page);
			var clickedPage = session.Page;
			var knownPageIdsBeforeClick = await GetContextPageIdsAsync(session.Context);
			if (!isPrefixedPoint)
			{
				var srcX = x;
				var srcY = y;
				var curView = await session.Page.EvaluateFunctionAsync<ViewportPoint>("() => ({ Vx: Math.round(window.scrollX || 0), Vy: Math.round(window.scrollY || 0), Vw: Math.max(0, window.innerWidth || 0), Vh: Math.max(0, window.innerHeight || 0) })");

				// Guard against transient zero viewport readings; fall back to known viewport/defaults.
				var safeViewW = curView.Vw > 0
					? curView.Vw
					: (session.ViewportWidth.GetValueOrDefault() > 0 ? session.ViewportWidth!.Value : 1152);
				var safeViewH = curView.Vh > 0
					? curView.Vh
					: (session.ViewportHeight.GetValueOrDefault() > 0 ? session.ViewportHeight!.Value : 2048);

				var refImageW = session.LastSnapshotImageWidth > 1 ? session.LastSnapshotImageWidth : Math.Max(1, safeViewW * 2);
				var refImageH = session.LastSnapshotImageHeight > 1 ? session.LastSnapshotImageHeight : Math.Max(1, safeViewH * 2);

				var vxScaled = (int)Math.Round(((double)srcX / Math.Max(1.0, refImageW)) * Math.Max(1, safeViewW));
				var vyScaled = (int)Math.Round(((double)srcY / Math.Max(1.0, refImageH)) * Math.Max(1, safeViewH));

				x = curView.Vx + Math.Clamp(vxScaled, 0, Math.Max(0, safeViewW - 1));
				y = curView.Vy + Math.Clamp(vyScaled, 0, Math.Max(0, safeViewH - 1));
				// click debug disabled: image->doc mapping
			}
			var vp = await session.Page.EvaluateFunctionAsync<ViewportPoint>(
				@"(x,y) => {
					const vw = Math.max(0, window.innerWidth || 0);
					const vh = Math.max(0, window.innerHeight || 0);
					const sx = (window.scrollX || 0);
					const sy = (window.scrollY || 0);

					// Convert document -> viewport coords *without* scrolling.
					let vx = Math.round(x - sx);
					let vy = Math.round(y - sy);

					// If point is off-screen, scroll just enough to bring it into view (near center).
					// In normal operation (P..,.. points are on-screen) this should not scroll.
					if (vx < 0 || vy < 0 || vx > vw || vy > vh) {
						const left = Math.max(0, Math.round(x - vw/2));
						const top  = Math.max(0, Math.round(y - vh/2));
						window.scrollTo(left, top);
						const sx2 = (window.scrollX || 0);
						const sy2 = (window.scrollY || 0);
						vx = Math.round(x - sx2);
						vy = Math.round(y - sy2);
					}

					return { vx, vy, vw, vh };
				}",
				(double)x, (double)y);

			// clamp inside viewport bounds (CDP can error if outside)
			var cx = Math.Min(Math.Max(vp.Vx, 1), Math.Max(1, vp.Vw - 1));
			var cy = Math.Min(Math.Max(vp.Vy, 1), Math.Max(1, vp.Vh - 1));

			// Give the scroll a frame (or two) to actually land before we click.
			await session.Page.EvaluateFunctionAsync(@"() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");

			// Start waits BEFORE clicking (covers full navigations + SPA updates)
			var navTask = session.Page.WaitForNavigationAsync(new NavigationOptions
			{
				WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
				Timeout = 15_000
			});

			// Start the DOM-mutation waiter *after* the click, because a full navigation destroys the old execution context.
			Task domTask = Task.CompletedTask;

			// Debug: confirm what element is actually under the cursor before clicking.
			try
			{
				_ = await session.Page.EvaluateFunctionAsync<string>(@"(x,y) => {
					const el = document.elementFromPoint(x, y);
					if (!el) return '(no elementFromPoint)';
					let txt = (el.innerText || el.getAttribute('aria-label') || el.title || '');
					txt = String(txt || '').replace(/\s+/g, ' ').trim();
					if (txt.length > 120) txt = txt.slice(0, 120);
					const id = el.id ? ('#' + el.id) : '';
					let cls = el.className ? String(el.className) : '';
					cls = cls.replace(/\s+/g, ' ').trim();
					cls = cls ? ('.' + cls.split(' ').filter(Boolean).slice(0,3).join('.')) : '';
					return el.tagName + id + cls + (txt ? (' | ' + txt) : '');
				}", cx, cy);
				// click debug disabled: click-hit probe
			}
			catch (Exception ex)
			{
				// click debug disabled: element probe exception
				/* best-effort */
			}

						// Debug: draw a persistent marker where we are about to click.
			try
			{
				await session.Page.EvaluateFunctionAsync(@"(x,y) => {
					const old = document.getElementById('__cfagent_click_marker');
					if (old) old.remove();

					const m = document.createElement('div');
					m.id = '__cfagent_click_marker';
					m.style.position = 'fixed';
					m.style.left = (x - 6) + 'px';
					m.style.top = (y - 6) + 'px';
					m.style.width = '18px';
					m.style.height = '18px';
					m.style.background = 'red';
					m.style.border = '2px solid white';
					m.style.borderRadius = '1px';
					m.style.boxShadow = '0 0 8px rgba(255,0,0,0.95)';
					m.style.pointerEvents = 'none';
					m.style.zIndex = '2147483647';
					document.documentElement.appendChild(m);

				}", cx, cy);
			}
			catch { /* best-effort */ }

			await session.Page.Mouse.MoveAsync(cx, cy);
			// Use native browser mouse events only (trusted click path).
			// This preserves normal bubbling to parent handlers and avoids synthetic mis-targeting.
			await session.Page.Mouse.ClickAsync(cx, cy, new ClickOptions { Delay = 30 });

			if (ensureFocus)
			{
				try
				{
					await session.Page.EvaluateFunctionAsync(@"(x,y) => {
						const el = document.elementFromPoint(x, y);
						if (!el) return false;
						try {
							if (el.tabIndex < 0) el.tabIndex = -1;
							el.focus({ preventScroll: true });
						} catch {
							try { el.focus(); } catch {}
						}
						return true;
					}", cx, cy);
				}
				catch { /* best-effort */ }
			}

			// Special case: clicking a select should return its options as choose commands.
			try
			{
				var dropdown = await TryGetDropdownAtPointAsync(session.Page, cx, cy);
				if (dropdown is not null && dropdown.IsSelect)
				{
					Console.WriteLine("current value: " + dropdown.Current);
					foreach (var opt in dropdown.Options)
					{
						Console.WriteLine("choose " + QuoteCommandArg(dropdown.Field) + " " + QuoteCommandArg(opt));
					}
					return;
				}
			}
			catch
			{
				// Best-effort only; continue with normal click flow.
			}

            // Popup special case: auto-switch into popup and auto-return when it closes.
            var popupPage = await WaitForNewPopupPageAsync(session, knownPageIdsBeforeClick, timeoutMs: 2500);
            if (popupPage is not null)
            {
                session.RegisterPopupReturn(popupPage, clickedPage);
                popupPage.Close += (_, _) =>
                {
                    try { session.TryAutoReturnFromPopup(popupPage); } catch { }
                };

                session.SetActivePage(popupPage);
                await WaitForDomChangeOrIdleAsync(popupPage, timeoutMs: 10_000, idleMs: 250);
                session.RecordHistory(beforeUrl, SafeGetUrl(session.Page));
                await DumpSnapshotAsync(session);
                try { await BrowserSession.PersistCookieJarAsync(session.Context, session.ProfileId); } catch { /* best-effort */ }
                return;
            }

			// If we didn't navigate, wait for some DOM mutation + idle settling.
			domTask = WaitForDomChangeOrIdleAsync(session.Page, timeoutMs: 15_000, idleMs: 300);

			// Wait for either: navigation, DOM mutation/idle, or timeout.
			try
			{
				await Task.WhenAny(navTask, domTask);
			}
			catch
			{
				// Swallow; we'll still dump whatever we can.
			}

			session.RecordHistory(beforeUrl, SafeGetUrl(session.Page));
			await DumpSnapshotAsync(session);

			// Persist cookies after clicks too (many SPAs set auth cookies post-click).
			try { await BrowserSession.PersistCookieJarAsync(session.Context, session.ProfileId); } catch { /* best-effort */ }
			return;
		}
		catch (Exception ex)
		{
			// If the click triggered a detach/close/crash, recycle the page so the session can continue.
			if (IsRecoverableClickFailure(ex))
			{
				Console.WriteLine($"warn: click failure looks recoverable ({ex.GetType().Name}: {ex.Message}). Recycling page.");
				await session.ReplacePageAsync(NewPageAsync);
				return;
			}

			Console.WriteLine($"error: Failed to perform mouse action ({ex.GetType().Name}: {ex.Message})");
		}

		static bool IsRecoverableClickFailure(Exception ex)
		{
			if (ex is TargetClosedException) return true;
			if (ex is NavigationException) return true;
			if (ex is NullReferenceException) return true;
			if (ex.Message.Contains("Object reference not set", StringComparison.OrdinalIgnoreCase)) return true;
			if (ex is PuppeteerException pex && pex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)) return true;
			if (ex is PuppeteerException pex2 && pex2.Message.Contains("Session closed", StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}
	}


	private static async Task<HashSet<string>> GetContextPageIdsAsync(BrowserContext context)
	{
		var ids = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			var pages = await context.PagesAsync();
			foreach (var p in pages)
			{
				var id = GetPageTargetId(p);
				if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
			}
		}
		catch { }
		return ids;
	}

	private static async Task<IPage?> WaitForNewPopupPageAsync(BrowserSession session, HashSet<string> knownPageIds, int timeoutMs)
	{
		var sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < timeoutMs)
		{
			try
			{
				var pages = await session.Context.PagesAsync();
				foreach (var p in pages)
				{
					if (p.IsClosed) continue;
					var id = GetPageTargetId(p);
					if (string.IsNullOrWhiteSpace(id)) continue;
					if (knownPageIds.Contains(id)) continue;
					return p;
				}
			}
			catch { }

			await Task.Delay(60);
		}

		return null;
	}

	private static string? GetPageTargetId(IPage page)
	{
		try { return page.Target.TargetId; } catch { return null; }
	}
	private async Task<DropdownClickResult?> TryGetDropdownAtPointAsync(IPage page, int viewportX, int viewportY)
	{
		return await page.EvaluateFunctionAsync<DropdownClickResult>(@"(x,y) => {
			const norm = (s) => String(s || '').replace(/\s+/g, ' ').trim();
			const has = (s) => norm(s).length > 0;
			const ascSelect = (el) => {
				let n = el;
				while (n) {
					if (n.tagName === 'SELECT') return n;
					n = n.parentElement;
				}
				return null;
			};

			let hit = null;
			try { hit = document.elementFromPoint(x, y); } catch {}
			let sel = ascSelect(hit);
			if (!sel && typeof document.elementsFromPoint === 'function') {
				try {
					const stack = document.elementsFromPoint(x, y) || [];
					for (const n of stack) {
						sel = ascSelect(n);
						if (sel) break;
					}
				} catch {}
			}

			if (!sel) return { isSelect: false, field: '', current: '', options: [] };

			const field = [
				sel.getAttribute('label-text'),
				sel.getAttribute('aria-label'),
				sel.name,
				sel.id
			].map(norm).find(has) || 'select';

			let current = '';
			try {
				if (sel.selectedIndex >= 0 && sel.options && sel.options[sel.selectedIndex]) {
					current = norm(sel.options[sel.selectedIndex].text || sel.options[sel.selectedIndex].value);
				}
			} catch {}
			if (!current) current = norm(sel.value);

			const options = [];
			const seen = new Set();
			try {
				for (const o of Array.from(sel.options || [])) {
					const text = norm((o && (o.text || o.label || o.value)) || '');
					if (!text) continue;
					const key = text.toLowerCase();
					if (seen.has(key)) continue;
					seen.add(key);
					options.push(text);
				}
			} catch {}

			return { isSelect: true, field, current, options };
		}", viewportX, viewportY);
	}

	private static string QuoteCommandArg(string value)
	{
		value ??= "";
		if (value.Length == 0) return "\"\"";
		var needsQuote = false;
		for (var i = 0; i < value.Length; i++)
		{
			var ch = value[i];
			if (char.IsWhiteSpace(ch) || ch == '"' || ch == '\'')
			{
				needsQuote = true;
				break;
			}
		}
		if (!needsQuote) return value;
		return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}

	private static List<string> TokenizeArgs(string input)
	{
		var tokens = new List<string>();
		if (string.IsNullOrWhiteSpace(input)) return tokens;

		var sb = new System.Text.StringBuilder();
		char quote = '\0';
		var escaping = false;

		for (var i = 0; i < input.Length; i++)
		{
			var ch = input[i];
			if (escaping)
			{
				sb.Append(ch);
				escaping = false;
				continue;
			}

			if (ch == '\\')
			{
				escaping = true;
				continue;
			}

			if (quote != '\0')
			{
				if (ch == quote)
				{
					quote = '\0';
				}
				else
				{
					sb.Append(ch);
				}
				continue;
			}

			if (ch == '"' || ch == '\'')
			{
				quote = ch;
				continue;
			}

			if (char.IsWhiteSpace(ch))
			{
				if (sb.Length > 0)
				{
					tokens.Add(sb.ToString());
					sb.Clear();
				}
				continue;
			}

			sb.Append(ch);
		}

		if (escaping) sb.Append('\\');
		if (sb.Length > 0) tokens.Add(sb.ToString());
		return tokens;
	}

	private async Task ChooseAsync(string args)
	{
		var session = TryGetActiveSession();
		if (session is null) return;

		var tokens = TokenizeArgs(args ?? string.Empty);
		if (tokens.Count < 2)
		{
			Console.WriteLine("error: choose requires <field> <option>");
			return;
		}

		var field = tokens[0];
		var choice = tokens[1];
		if (tokens.Count > 2)
		{
			var sb = new System.Text.StringBuilder(choice);
			for (var i = 2; i < tokens.Count; i++)
			{
				sb.Append(' ').Append(tokens[i]);
			}
			choice = sb.ToString();
		}

		try
		{
			var result = await session.Page.EvaluateFunctionAsync<ChooseResult>(@"(field, optionText) => {
				const norm = (s) => String(s || '').replace(/\s+/g, ' ').trim();
				const lower = (s) => norm(s).toLowerCase();
				const key = lower(field);
				const optKey = lower(optionText);
				if (!key) return { ok: false, error: 'missing field', current: '' };
				if (!optKey) return { ok: false, error: 'missing option', current: '' };

				const all = Array.from(document.querySelectorAll('select'));
				if (all.length === 0) return { ok: false, error: 'no select elements found', current: '' };

				const scoreSelect = (sel) => {
					const vals = [
						norm(sel.id),
						norm(sel.name),
						norm(sel.getAttribute('label-text')),
						norm(sel.getAttribute('aria-label'))
					];

					let score = -1;
					for (const v of vals) {
						const lv = lower(v);
						if (!lv) continue;
						if (lv === key) score = Math.max(score, 100);
						else if (lv.startsWith(key)) score = Math.max(score, 80);
						else if (lv.includes(key) || key.includes(lv)) score = Math.max(score, 60);
					}
					return score;
				};

				let sel = null;
				let best = -1;
				for (const s of all) {
					const sc = scoreSelect(s);
					if (sc > best) {
						best = sc;
						sel = s;
					}
				}

				if (!sel || best < 0) {
					return { ok: false, error: 'select not found for field: ' + field, current: '' };
				}

				const options = Array.from(sel.options || []);
				if (options.length === 0) return { ok: false, error: 'select has no options', current: '' };

				let target = null;
				let bestOpt = -1;
				for (const o of options) {
					const label = norm(o.text || o.label || o.value || '');
					const value = norm(o.value || '');
					const l1 = lower(label);
					const l2 = lower(value);
					let sc = -1;
					if (l1 === optKey || l2 === optKey) sc = 100;
					else if (l1.startsWith(optKey) || l2.startsWith(optKey)) sc = 80;
					else if (l1.includes(optKey) || l2.includes(optKey) || optKey.includes(l1) || optKey.includes(l2)) sc = 60;
					if (sc > bestOpt) {
						bestOpt = sc;
						target = o;
					}
				}

				if (!target || bestOpt < 0) {
					return { ok: false, error: 'option not found: ' + optionText, current: '' };
				}

				sel.value = target.value;
				target.selected = true;
				sel.selectedIndex = options.indexOf(target);

				try { sel.dispatchEvent(new Event('input', { bubbles: true })); } catch {}
				try { sel.dispatchEvent(new Event('change', { bubbles: true })); } catch {}
				try {
					if (typeof window.change === 'function') {
						window.change(field);
					}
				} catch {}

				let current = norm(target.text || target.label || target.value || '');
				if (!current) current = norm(sel.value || '');
				return { ok: true, error: '', current };
			}", field, choice);

			if (result is null || !result.Ok)
			{
				Console.WriteLine("error: choose failed" + (result is not null && !string.IsNullOrWhiteSpace(result.Error) ? " - " + result.Error : ""));
				return;
			}

			Console.WriteLine("current value: " + result.Current);
			await DumpSnapshotAsync(session);
			try { await BrowserSession.PersistCookieJarAsync(session.Context, session.ProfileId); } catch { /* best-effort */ }
		}
		catch (Exception ex)
		{
			Console.WriteLine("error: choose failed (" + ex.GetType().Name + ": " + ex.Message + ")");
		}
	}

	private async Task SendKeysAsync(string args)
	{
		// Sends characters one at a time (useful when pages react per-keystroke).
		var session = TryGetActiveSession();
		if (session is null) return;

		if (!IsPageHealthy(session.Page))
		{
			Console.WriteLine("warn: page appears unhealthy before send-keys; recreating");
			await session.ReplacePageAsync(NewPageAsync);
		}

		try
		{
			foreach (var ch in args ?? string.Empty)
			{
				await SendCharacterCompatAsync(session.Page.Keyboard, ch);
				await Task.Delay(5);
			}
			Console.WriteLine("ok");
		}
		catch (Exception ex)
		{
			Console.WriteLine("error: send-keys failed (" + ex.GetType().Name + ": " + ex.Message + ")");
		}
	}

	private static async Task SendCharacterCompatAsync(IKeyboard keyboard, char ch)
	{
		// Prefer SendCharacterAsync if present (PuppeteerSharp version dependent).
		var m = keyboard.GetType().GetMethod("SendCharacterAsync", new[] { typeof(string) });
		if (m is not null)
		{
			var taskObj = m.Invoke(keyboard, new object[] { ch.ToString() });
			if (taskObj is Task t) { await t.ConfigureAwait(false); return; }
		}

		// Fallback: type one char.
		await keyboard.TypeAsync(ch.ToString());
	}

	private async Task SendEnterAsync()
	{
		// Sends Enter to the currently-focused element.
		// Behaves like click: wait for navigation or DOM mutation, then dump.
		var session = TryGetActiveSession();
		if (session is null) return;

		if (!IsPageHealthy(session.Page))
		{
			Console.WriteLine("warn: page appears unhealthy before send-enter; recreating");
			await session.ReplacePageAsync(NewPageAsync);
		}

		try
		{
			var navTask = session.Page.WaitForNavigationAsync(new NavigationOptions
			{
				WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
				Timeout = 15_000
			});

			await session.Page.Keyboard.PressAsync(Key.Enter);

			var domTask = WaitForDomChangeOrIdleAsync(session.Page, timeoutMs: 15_000, idleMs: 300);
			try { await Task.WhenAny(navTask, domTask); } catch { }

			await Task.Delay(2000);
			await DumpSnapshotAsync(session);
			try { await BrowserSession.PersistCookieJarAsync(session.Context, session.ProfileId); } catch { }

			Console.WriteLine("ok");
		}
		catch (Exception ex)
		{
			Console.WriteLine("error: send-enter failed (" + ex.GetType().Name + ": " + ex.Message + ")");
		}
	}

	private async Task ShowPointAsync(string args)
	{
		// Debug aid: draws a red square (top z-index) at the given document point Pxxx,yyy.
		// Also shows a short description of the element currently under that point.
		if (string.IsNullOrWhiteSpace(args))
		{
			Console.WriteLine("error: show-point requires a point like P150,120");
			return;
		}

		var session = TryGetActiveSession();
		if (session is null) return;

		if (!TryParsePoint(args.Trim(), out var x, out var y))
		{
			Console.WriteLine("error: show-point requires a point like P150,120");
			return;
		}

		if (!IsPageHealthy(session.Page))
		{
			Console.WriteLine("warn: page appears unhealthy before show-point; recreating");
			await session.ReplacePageAsync(NewPageAsync);
		}

		try
		{
			var res = await session.Page.EvaluateFunctionAsync<ShowPointResult>(
				@"(x,y) => {
					const vw = Math.max(0, window.innerWidth || 0);
					const vh = Math.max(0, window.innerHeight || 0);
					let sx = (window.scrollX || 0);
					let sy = (window.scrollY || 0);

					let vx = Math.round(x - sx);
					let vy = Math.round(y - sy);

					// If off-screen, scroll to bring it roughly centered.
					if (vx < 0 || vy < 0 || vx > vw || vy > vh) {
						const left = Math.max(0, Math.round(x - vw/2));
						const top  = Math.max(0, Math.round(y - vh/2));
						window.scrollTo(left, top);
						sx = (window.scrollX || 0);
						sy = (window.scrollY || 0);
						vx = Math.round(x - sx);
						vy = Math.round(y - sy);
					}

					// Clamp inside viewport bounds
					const cx = Math.min(Math.max(vx, 1), Math.max(1, vw - 1));
					const cy = Math.min(Math.max(vy, 1), Math.max(1, vh - 1));

					// Element description
					let desc = '(no elementFromPoint)';
					try {
						const el = document.elementFromPoint(cx, cy);
						if (el) {
							let txt = (el.innerText || el.getAttribute('aria-label') || el.title || '');
							txt = String(txt || '').replace(/\s+/g, ' ').trim();
							if (txt.length > 160) txt = txt.slice(0, 160);
							const id = el.id ? ('#' + el.id) : '';
							let cls = el.className ? String(el.className) : '';
							cls = cls.replace(/\s+/g, ' ').trim();
							cls = cls ? ('.' + cls.split(' ').filter(Boolean).slice(0,3).join('.')) : '';
							desc = el.tagName + id + cls + (txt ? (' | ' + txt) : '');
						}
					} catch {}

					// Remove prior marker
					try {
						const old = document.getElementById('__cfagent_point');
						if (old) old.remove();
					} catch {}

					// Create marker container
					const host = document.createElement('div');
					host.id = '__cfagent_point';
					host.style.position = 'fixed';
					host.style.left = '0px';
					host.style.top = '0px';
					host.style.width = '0px';
					host.style.height = '0px';
					host.style.zIndex = '2147483647';
					host.style.pointerEvents = 'none';

					const box = document.createElement('div');
					box.style.position = 'fixed';
					box.style.left = (cx - 4) + 'px';
					box.style.top  = (cy - 4) + 'px';
					box.style.width = '8px';
					box.style.height = '8px';
					box.style.background = 'red';
					box.style.border = '1px solid white';
					box.style.borderRadius = '1px';
					box.style.boxShadow = '0 0 6px rgba(255,0,0,0.9)';

					const label = document.createElement('div');
					label.style.position = 'fixed';
					label.style.left = Math.min(Math.max(8, cx + 10), Math.max(8, vw - 260)) + 'px';
					label.style.top  = Math.min(Math.max(8, cy + 10), Math.max(8, vh - 60)) + 'px';
					label.style.maxWidth = '240px';
					label.style.padding = '6px 8px';
					label.style.background = 'rgba(0,0,0,0.82)';
					label.style.color = 'white';
					label.style.font = '12px/1.25 Arial, sans-serif';
					label.style.border = '1px solid rgba(255,255,255,0.25)';
					label.style.borderRadius = '6px';
					label.style.boxShadow = '0 6px 18px rgba(0,0,0,0.35)';
					label.textContent = 'P' + Math.round(x) + ',' + Math.round(y) + ' → ' + desc;

					host.appendChild(box);
					host.appendChild(label);
					document.documentElement.appendChild(host);

					return { ok: true, vx: cx, vy: cy, sx: Math.round(sx), sy: Math.round(sy), desc: desc };
				}",
				(double)x, (double)y);

			if (res is null || !res.Ok)
			{
				Console.WriteLine("error: show-point failed");
				return;
			}

			Console.WriteLine($"ok P{(int)x},{(int)y} -> viewport {res.Vx},{res.Vy} | {res.Desc}");
		}
		catch (Exception ex)
		{
			Console.WriteLine("error: show-point failed (" + ex.GetType().Name + ": " + ex.Message + ")");
		}
	}

	private sealed class ShowPointResult
	{
		public bool Ok { get; set; }
		public int Vx { get; set; }
		public int Vy { get; set; }
		public int Sx { get; set; }
		public int Sy { get; set; }
		public string Desc { get; set; } = "";
	}

		private static bool IsPrefixedPoint(string s)
	{
		if (string.IsNullOrWhiteSpace(s)) return false;
		s = s.Trim();
		if (s.StartsWith("(", StringComparison.Ordinal) && s.Length > 1)
		{
			s = s[1..].TrimStart();
		}
		return s.StartsWith("P", StringComparison.OrdinalIgnoreCase);
	}
static bool TryParsePoint(string s, out decimal x, out decimal y)
	{
		x = 0;
		y = 0;

		// Accept: P150,120  |  (P150,120)  |  150,120
		s = s.Trim();
		if (s.StartsWith("(") && s.EndsWith(")") && s.Length > 2)
		{
			s = s[1..^1].Trim();
		}
		if (s.StartsWith("P", StringComparison.OrdinalIgnoreCase))
		{
			s = s[1..].Trim();
		}

		var comma = s.IndexOf(',');
		if (comma <= 0 || comma >= s.Length - 1) return false;

		var xs = s[..comma].Trim();
		var ys = s[(comma + 1)..].Trim();
		return decimal.TryParse(xs, out x) && decimal.TryParse(ys, out y);
	}

	private sealed class ViewportPoint
	{
		public int Vx { get; set; }
		public int Vy { get; set; }
		public int Vw { get; set; }
		public int Vh { get; set; }
	}

	private async Task PrintScrollablesAsync(BrowserSession session, bool includeLinks)
	{
		// Single source of truth for scrollable target listing + synthetic scroll links.
		// NOTE: Caller decides whether scroll targets should be refreshed beforehand.
		var st = session.ScrollTargets;
		if (st.Count == 0) return;

		var sbScroll = new System.Text.StringBuilder();
		sbScroll.Append("SCROLLABLES: ");
		var first = true;
		foreach (var t in st)
		{
			if (!first) sbScroll.Append(" | ");
			first = false;
			sbScroll.Append(t.Code).Append('=');
			sbScroll.Append(t.Kind == "document" ? "document" : (t.Description ?? "element"));
			sbScroll.Append(' ');
			sbScroll.Append(t.ScrollTop).Append('/').Append(t.MaxScrollTop);
		}
		Console.WriteLine(sbScroll.ToString());

		if (!includeLinks) return;

		foreach (var t in st)
		{
			// In debug mode we may list containers that *claim* to be scrollable (overflow-y/overflow)
			// even if scrollHeight/clientHeight don't yet reflect it (AJAX, virtualization, custom scrollbars).
			// So: always offer scroll actions for element targets, and offer doc actions only when they can move.

			if (t.Kind == "document")
			{
				if (t.MaxScrollTop <= 0) continue;
				if (t.ScrollTop > 0)
					Console.WriteLine($"[link] scroll-to {t.Code} up");
				if (t.ScrollTop < t.MaxScrollTop)
					Console.WriteLine($"[link] scroll-to {t.Code} down");
				continue;
			}

			// Element targets: emit "down" even when MaxScrollTop is 0, because the numbers can be stale
			// or the real scrollable element may be a nested descendant (we resolve by point at scroll time).
			if (t.ScrollTop > 0)
				Console.WriteLine($"[link] scroll-to {t.Code} up");
			Console.WriteLine($"[link] scroll-to {t.Code} down");
		}
	}

	private async Task DumpSnapshotAsync(BrowserSession session)
	{
		// Refresh click/URL tables so snapshot can annotate items with (P..,..) and resolve link codes.
		// Stage 1 of scrolling: detect scrollable containers (including document).
		// IMPORTANT: do this BEFORE building text->point maps so duplicates can be assigned to the most specific scroll region.
		await session.UpdateScrollTargetsFromDomAsync();

		// Refresh click/URL tables so snapshot can annotate items with (P..,..) and resolve link codes.
		await session.UpdateLinkTableFromDomAsync();

		Console.WriteLine($"URL: {SafeGetUrl(session.Page)}");

		// Scrollable containers + actionable synthetic scroll links.
		await PrintScrollablesAsync(session, includeLinks: true);

		if (useAlternativeInteractableProbe)
		{
			if (await TryDumpAlternativeSnapshotAsync(session, maxLinesPerGroup: 800, maxElements: 1200, unrestrictedText: false, compactOutput: false))
				return;

			Console.WriteLine("warn: --alt probe failed; falling back to default snapshot");
		}

		try
		{
			var client = await session.Page.Target.CreateCDPSessionAsync();
			var result = await client.SendAsync("Accessibility.getFullAXTree");
			var elem = result is JsonElement je ? je : JsonSerializer.SerializeToElement(result);

			var pruned = AxRawFormatter.FormatGrouped(elem, maxLines: 800, session: session);
			pruned = ReplaceUrlsWithCodes(pruned, session);
			PrintDumpText(session, pruned, compactOutput: true);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"warn: raw accessibility dump failed ({ex.GetType().Name}: {ex.Message}); falling back to DOM snapshot");
			var fallback = await BuildDomFallbackSnapshotAsync(session, ex);
			PrintDumpText(session, fallback, compactOutput: true);
		}
	}

	private async Task<bool> TryDumpAlternativeSnapshotAsync(BrowserSession session, int maxLinesPerGroup, int maxElements, bool unrestrictedText, bool compactOutput = true)
	{
		try
		{
			var items = await AlternativeInteractableTextGatherer.CollectAsync(
				session.Page,
				maxElements: maxElements,
				unrestrictedText: unrestrictedText);

			var grouped = new Dictionary<string, List<AlternativeInteractableTextGatherer.InteractableElement>>(StringComparer.OrdinalIgnoreCase);
			foreach (var t in session.ScrollTargets)
			{
				grouped[t.Code] = new List<AlternativeInteractableTextGatherer.InteractableElement>();
			}

			var fallbackCode = session.ScrollTargets.FirstOrDefault(t => t.Kind == "document")?.Code
				?? session.ScrollTargets.FirstOrDefault()?.Code
				?? "S1";
			if (!grouped.ContainsKey(fallbackCode))
			{
				grouped[fallbackCode] = new List<AlternativeInteractableTextGatherer.InteractableElement>();
			}

			foreach (var it in items)
			{
				var vx = it.X - session.WindowScrollX;
				var vy = it.Y - session.WindowScrollY;
				var code = session.ResolveScrollTargetForViewportPoint(vx, vy) ?? fallbackCode;
				if (!grouped.TryGetValue(code, out var list))
				{
					list = new List<AlternativeInteractableTextGatherer.InteractableElement>();
					grouped[code] = list;
				}
				list.Add(it);
			}

			var sbAlt = new System.Text.StringBuilder(16 * 1024);
			foreach (var t in session.ScrollTargets.OrderBy(t => t.Kind == "document" ? 0 : 1).ThenBy(t => t.Code, StringComparer.OrdinalIgnoreCase))
			{
				if (!grouped.TryGetValue(t.Code, out var list) || list.Count == 0) continue;
				list = list
					.OrderBy(i => i.Y)
					.ThenBy(i => i.X)
					.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
					.ToList();
				sbAlt.AppendLine($"-- {t.Code} {(t.Kind == "document" ? "document" : (t.Description ?? "element"))} {t.ScrollTop}/{t.MaxScrollTop} --");
				sbAlt.Append(AlternativeInteractableTextGatherer.ToSnapshotText(list, maxLines: maxLinesPerGroup));
			}

			foreach (var kvp in grouped.Where(k => session.ScrollTargets.All(t => !t.Code.Equals(k.Key, StringComparison.OrdinalIgnoreCase))))
			{
				if (kvp.Value.Count == 0) continue;
				var list = kvp.Value
					.OrderBy(i => i.Y)
					.ThenBy(i => i.X)
					.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
					.ToList();
				sbAlt.AppendLine($"-- {kvp.Key} element 0/0 --");
				sbAlt.Append(AlternativeInteractableTextGatherer.ToSnapshotText(list, maxLines: maxLinesPerGroup));
			}

			PrintDumpText(session, sbAlt.ToString(), compactOutput);
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"warn: alt probe failed ({ex.GetType().Name}: {ex.Message})");
			return false;
		}
	}

	private void PrintDumpText(BrowserSession session, string text, bool compactOutput)
	{
		text ??= string.Empty;
		if (!compactOutput)
		{
			Console.WriteLine(text);
			return;
		}

		var prev = session.LastDumpText;
		session.LastDumpText = text;

		if (string.IsNullOrEmpty(prev))
		{
			Console.WriteLine(text);
			return;
		}

		var curLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		var prevLines = prev.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		var head = 0;
		var maxHead = Math.Min(curLines.Length, prevLines.Length);
		while (head < maxHead && string.Equals(curLines[head], prevLines[head], StringComparison.Ordinal))
		{
			head++;
		}

		var tail = 0;
		while (tail < (curLines.Length - head)
			&& tail < (prevLines.Length - head)
			&& string.Equals(curLines[curLines.Length - 1 - tail], prevLines[prevLines.Length - 1 - tail], StringComparison.Ordinal))
		{
			tail++;
		}

		// No line-level changes.
		if (head == curLines.Length && head == prevLines.Length)
		{
			Console.WriteLine("(no snapshot text changes)");
			return;
		}

		var sb = new System.Text.StringBuilder(text.Length);
		if (head > 0)
		{
			sb.AppendLine($"... [{head} unchanged lines omitted at top]");
		}

		for (var i = head; i < curLines.Length - tail; i++)
		{
			sb.AppendLine(curLines[i]);
		}

		if (tail > 0)
		{
			sb.AppendLine($"... [{tail} unchanged lines omitted at bottom]");
		}

		Console.WriteLine(sb.ToString());
	}
	private async Task<string> BuildDomFallbackSnapshotAsync(BrowserSession session, Exception? axError)
	{
		try
		{
			// Give late-loading layouts (SPA/nav panes) a tick to settle before measuring scrollability.
			await session.Page.EvaluateFunctionAsync(@"() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");

			var items = await session.Page.EvaluateFunctionAsync<DomFallbackItem[]>(@"() => {
  const norm = (s) => {
    if (!s) return '';
    return String(s).replace(/\s+/g, ' ').trim();
  };

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
    } catch { return false; }
  };

  const rectCenterInView = (el) => {
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
      x = Math.min(Math.max(x, 1), Math.max(1, vw - 2));
      y = Math.min(Math.max(y, 1), Math.max(1, vh - 2));

      // Basic occlusion hit-test
      try {
        const hit = document.elementFromPoint(x, y);
        if (hit && !(el === hit || el.contains(hit) || hit.contains(el))) return null;
      } catch {}

      return { x, y };
    } catch { return null; }
  };

  const label = (el, fallback) => {
    try {
      // innerText is great for normal elements, but inputs/textareas often have empty innerText.
      const tag = (el && el.tagName ? String(el.tagName).toLowerCase() : '');
      if (tag === 'input' || tag === 'textarea' || tag === 'select') {
        const v = norm(el.value);
        const ph = norm(el.getAttribute && el.getAttribute('placeholder'));
        const al = norm(el.getAttribute && el.getAttribute('aria-label'));
        const ti = norm(el.title);
        return v || ph || al || ti || norm(fallback) || '';
      }
    } catch {}

    // Generic elements
    return norm(el.innerText) || norm(el.getAttribute('aria-label')) || norm(el.title) || norm(fallback) || '';
  };

  const out = [];
  const push = (role, el, href) => {
    const pt = rectCenterInView(el);
    if (!pt) return;
    const text = label(el, href);
    if (!text) return;
    out.push({ role: role, text: text, href: href || '' });
  };

  // Clickables first
  for (const a of Array.from(document.querySelectorAll('a[href]')))
    if (a.href && a.href !== '#') push('link', a, a.href);

  for (const b of Array.from(document.querySelectorAll('button, input[type=button], input[type=submit], [role=button]')))
    push('button', b, '');

  for (const c of Array.from(document.querySelectorAll('[onclick]')))
    push('button', c, '');

  // Visible-ish text anchors (context when AX fails)
  const textTags = Array.from(document.querySelectorAll('h1,h2,h3,h4,h5,h6,label,p,li,dt,dd,caption,figcaption'));
  for (const el of textTags) {
    const pt = rectCenterInView(el);
    if (!pt) continue;
    let t = norm(el.innerText);
    if (!t) continue;
    if (t.length > 120) t = t.slice(0, 120);
    out.push({ role: 'text', text: t, href: '' });
  }

  // De-dupe
  const seen = new Set();
  const uniq = [];
  for (const it of out) {
    const k = (it.role||'') + '|' + (it.text||'') + '|' + (it.href||'');
    if (seen.has(k)) continue;
    seen.add(k);
    uniq.push(it);
    if (uniq.length >= 500) break;
  }

  return uniq;
}");

			var sb = new System.Text.StringBuilder(8 * 1024);
			if (axError is not null)
			{
				sb.Append("(no accessibility snapshot; DOM fallback)");
			}
			else
			{
				sb.Append("(no accessibility snapshot; DOM fallback)");
			}

			if (items is null || items.Length == 0)
			{
				sb.Append("(no DOM fallback items)");
				return sb.ToString();
			}

			foreach (var it in items)
			{
				if (string.IsNullOrWhiteSpace(it.Text)) continue;

				if (it.Role.Equals("text", StringComparison.OrdinalIgnoreCase))
				{
					sb.Append("[text] ").Append(it.Text).Append("\r\n");
					continue;
				}

				sb.Append('[').Append(it.Role).Append("] ").Append(it.Text);

				if (!string.IsNullOrWhiteSpace(it.Href))
				{
					var code = session.GetOrAddLinkCode(it.Href);
					sb.Append(' ').Append(code);
				}

				// Best-effort click point annotation via our DOM click table
				if (session.TryResolvePointByText(it.Text, out var pt) || session.TryResolvePointByTextFuzzy(it.Text, out pt))
				{
					sb.Append(" (P").Append(pt.X).Append(',').Append(pt.Y).Append(')');
				}

				sb.Append("\r\n");
			}

			return sb.ToString();
		}
		catch (Exception ex)
		{
			return $"(no accessibility snapshot; DOM fallback failed: {ex.GetType().Name}: {ex.Message})";
		}
	}

	private sealed class DomFallbackItem
	{
		public string Role { get; set; } = "";
		public string Text { get; set; } = "";
		public string Href { get; set; } = "";
	}

	private static string SafeGetUrl(IPage page)
	{
		try
		{
			return page.Url ?? "(unknown)";
		}
		catch
		{
			return "(unknown)";
		}
	}

	private static string ToFileUri(string path)
	{
		try
		{
			return new Uri(Path.GetFullPath(path)).AbsoluteUri;
		}
		catch
		{
			return path;
		}
	}

	private static Task WaitForDomChangeOrIdleAsync(IPage page, int timeoutMs, int idleMs)
	{
		// Wait until the DOM mutates AND then goes idle for `idleMs` OR until `timeoutMs`.
		// This is a good fit for SPAs where clicks trigger async renders without full navigation.
		return page.EvaluateFunctionAsync(@"(timeoutMs, idleMs) => {
      return new Promise(resolve => {
        let done = false;
        let idleTimer = null;
        const finish = () => {
          if (done) return;
          done = true;
          try { obs && obs.disconnect(); } catch {}
          try { resolve(true); } catch {}
        };

        const startIdle = () => {
          if (idleTimer) clearTimeout(idleTimer);
          idleTimer = setTimeout(finish, Math.max(0, idleMs|0));
        };

        // If we get no mutations at all, we still resolve at timeout.
        const hardTimeout = setTimeout(() => {
          if (idleTimer) clearTimeout(idleTimer);
          finish();
        }, Math.max(0, timeoutMs|0));

        let obs = null;
        try {
          obs = new MutationObserver(() => {
            // First mutation -> wait for things to settle.
            startIdle();
          });
          obs.observe(document.documentElement || document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            characterData: true
          });
        } catch {
          // If MutationObserver isn't available for some reason, just idle-wait.
          startIdle();
        }

        // Also give a small initial idle window in case changes happen synchronously.
        startIdle();
      });
    }", timeoutMs, idleMs);
	}

	private async Task EnterTextAsync(string args)
	{
		if (string.IsNullOrEmpty(args))
		{
			Console.WriteLine("error: enter-text requires text");
			return;
		}

		var session = TryGetActiveSession();
		if (session is null)
		{
			return;
		}

		await session.Page.Keyboard.TypeAsync(args);
		Console.WriteLine("ok");
	}

	private static async Task EnsureViewportAsync(BrowserSession session, int width, int height)
	{
		if (session.ViewportWidth == width && session.ViewportHeight == height)
		{
			return;
		}

		await session.Page.SetViewportAsync(new ViewPortOptions
		{
			Width = width,
			Height = height
		});

		session.ViewportWidth = width;
		session.ViewportHeight = height;
	}

	public async ValueTask DisposeAsync()
	{
		// Clear pid file on a clean shutdown (we'll recreate it on next launch).
		TryDeleteBrowserPidFile();
		// Persist cookies on shutdown so the next run can resume authenticated sessions.
		try
		{
			var session = TryGetActiveSession();
			if (session is not null)
			{
				await BrowserSession.PersistCookieJarAsync(session.Context, session.ProfileId);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"warn: cookie persist failed ({ex.GetType().Name}: {ex.Message})");
		}

		foreach (var session in sessions.Values)
		{
			await session.Context.CloseAsync();
		}

		sessions.Clear(); if (browser is not null)
		{
			try
			{
				await browser.CloseAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"warn: browser.CloseAsync failed ({ex.GetType().Name}: {ex.Message}); attempting process cleanup");
				// If close fails, try to kill whatever we recorded.
				CleanupDanglingChromiumFromPidFile();
			}
			finally
			{
				try { await browser.DisposeAsync(); } catch { }
			}
		}
	}

	public sealed class BrowserSession
	{
		// Cookie persistence happens at the BrowserContext level (session-wide).
		public BrowserSession(Guid id, string profileId, BrowserContext context, IPage page)
		{
			Id = id;
			ProfileId = NormalizeProfileId(profileId);
			Context = context;
			Page = page;
			LastTouchedUtc = DateTime.UtcNow;
		}

		public Guid Id { get; }
		public string ProfileId { get; }
		public BrowserContext Context { get; }
		public IPage Page { get; private set; }
		public SemaphoreSlim CommandGate { get; } = new(1, 1);
		public int ActiveCommandCount { get; set; }
		public DateTime LastTouchedUtc { get; set; }
		public int? ViewportWidth { get; set; }
		public int? ViewportHeight { get; set; }
		public int LastSnapshotImageWidth { get; set; }
		public int LastSnapshotImageHeight { get; set; }

		public Dictionary<string, string> LinkCodeToUrl { get; } = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, string> UrlToLinkCode { get; } = new(StringComparer.OrdinalIgnoreCase);

		// Best-effort mapping from visible link text -> href, built from the DOM.
		public Dictionary<string, string> LinkTextToUrl { get; } = new(StringComparer.OrdinalIgnoreCase);

		// Best-effort mapping from visible clickable text -> click point (center of bounding rect)
		public Dictionary<string, ClickPoint> ClickTextToPoint { get; } = new(StringComparer.OrdinalIgnoreCase);

		// When multiple elements share the same visible text, we keep the one that belongs to the most specific (smallest-area) scroll region.
		private Dictionary<string, long> ClickTextToPointScore { get; } = new(StringComparer.OrdinalIgnoreCase);

		// Visible (viewport-intersecting) text anchors (including non-clickable text), used for on-screen pruning.
		public Dictionary<string, ClickPoint> VisibleTextToPoint { get; } = new(StringComparer.OrdinalIgnoreCase);
		private Dictionary<string, long> VisibleTextToPointScore { get; } = new(StringComparer.OrdinalIgnoreCase);
		public int NextLinkId { get; set; } = 1;

		// Scroll targets (viewport-visible scroll containers + document)
		public int NextScrollId { get; set; } = 1;
		public Dictionary<string, ScrollTarget> ScrollCodeToTarget { get; } = new(StringComparer.OrdinalIgnoreCase);
		private Dictionary<string, string> ScrollFingerprintToCode { get; } = new(StringComparer.Ordinal);
		public IReadOnlyList<ScrollTarget> ScrollTargets => ScrollCodeToTarget.Values.OrderBy(t => t.Code, StringComparer.OrdinalIgnoreCase).ToArray();

		public int WindowScrollX { get; private set; }
		public int WindowScrollY { get; private set; }
		public string? LastDumpText { get; set; }
		private Stack<string> UrlHistory { get; } = new();
		private Dictionary<string, IPage> PopupReturnByPopupTargetId { get; } = new(StringComparer.Ordinal);

		public void RecordHistory(string? beforeUrl, string? afterUrl)
		{
			if (string.IsNullOrWhiteSpace(beforeUrl)) return;
			if (beforeUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;
			if (string.Equals(beforeUrl, afterUrl, StringComparison.OrdinalIgnoreCase)) return;
			if (UrlHistory.Count > 0 && string.Equals(UrlHistory.Peek(), beforeUrl, StringComparison.OrdinalIgnoreCase)) return;
			UrlHistory.Push(beforeUrl);
		}

		public bool TryPopHistory(out string url)
		{
			url = "";
			while (UrlHistory.Count > 0)
			{
				var candidate = UrlHistory.Pop();
				if (string.IsNullOrWhiteSpace(candidate)) continue;
				if (candidate.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) continue;
				url = candidate;
				return true;
			}
			return false;
		}

		public string? ResolveScrollTargetForViewportPoint(int vx, int vy)
		{
			// Choose the most specific (smallest area) element scroll container whose viewport rect contains the point.
			// Fallback to the document scroller.
			ScrollTarget? best = null;
			var bestArea = long.MaxValue;
			foreach (var t in ScrollCodeToTarget.Values)
			{
				if (t.Kind == "document") continue;
				if (vx < t.X || vy < t.Y || vx > (t.X + t.W) || vy > (t.Y + t.H)) continue;
				var area = (long)t.W * (long)t.H;
				if (area < bestArea)
				{
					bestArea = area;
					best = t;
				}
			}

			if (best is not null) return best.Code;

			var doc = ScrollCodeToTarget.Values.FirstOrDefault(t => t.Kind == "document");
			return doc?.Code;
		}

		public string GetOrAddLinkCode(string url)
		{
			if (UrlToLinkCode.TryGetValue(url, out var existing))
				return existing;

			var code = $"L{NextLinkId++}";
			UrlToLinkCode[url] = code;
			LinkCodeToUrl[code] = url;
			return code;
		}

		public async Task ReplacePageAsync(Func<IBrowserContext, Task<IPage>> pageFactory)
		{
			try
			{
				if (!Page.IsClosed)
				{
					await Page.CloseAsync();
				}
			}
			catch
			{
				// Best-effort cleanup.
			}

			Page = await pageFactory(Context);
			ResetForNewActivePage();
			PopupReturnByPopupTargetId.Clear();
		}

		public void SetActivePage(IPage page)
		{
			Page = page;
			ResetForNewActivePage();
		}

		public void RegisterPopupReturn(IPage popupPage, IPage returnToPage)
		{
			var popupId = TryGetTargetId(popupPage);
			if (string.IsNullOrWhiteSpace(popupId)) return;
			PopupReturnByPopupTargetId[popupId] = returnToPage;
		}

		public void TryAutoReturnFromPopup(IPage popupPage)
		{
			var popupId = TryGetTargetId(popupPage);
			if (string.IsNullOrWhiteSpace(popupId)) return;

			if (!PopupReturnByPopupTargetId.TryGetValue(popupId, out var returnToPage)) return;
			PopupReturnByPopupTargetId.Remove(popupId);

			if (TryGetTargetId(Page) != popupId) return;
			if (returnToPage.IsClosed) return;

			SetActivePage(returnToPage);
		}

		private void ResetForNewActivePage()
		{
			ViewportWidth = null;
			ViewportHeight = null;
			LastSnapshotImageWidth = 0;
			LastSnapshotImageHeight = 0;

			LinkTextToUrl.Clear();
			ClickTextToPoint.Clear();
			ClickTextToPointScore.Clear();
			VisibleTextToPoint.Clear();
			VisibleTextToPointScore.Clear();
			ScrollCodeToTarget.Clear();
			ScrollFingerprintToCode.Clear();
			NextScrollId = 1;
			LastDumpText = null;

			ClickTextToPointScore.Clear();
			VisibleTextToPointScore.Clear();
		}

		private static string? TryGetTargetId(IPage page)
		{
			try { return page.Target.TargetId; } catch { return null; }
		}

		public bool TryResolveUrlByLinkText(string linkText, out string url)
			=> LinkTextToUrl.TryGetValue(Norm(linkText), out url);

		public bool TryResolvePointByText(string text, out ClickPoint pt)
			=> ClickTextToPoint.TryGetValue(Norm(text), out pt);

		public bool TryResolveVisiblePointByText(string text, out ClickPoint pt)
			=> VisibleTextToPoint.TryGetValue(Norm(text), out pt);

		public bool TryResolvePointByTextFuzzy(string text, out ClickPoint pt)
		{
			pt = default;
			var key = Norm(text);
			if (key.Length == 0) return false;

			foreach (var kvp in ClickTextToPoint)
				if (kvp.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase))
				{ pt = kvp.Value; return true; }

			string? bestKey = null;
			foreach (var kvp in ClickTextToPoint)
				if (kvp.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
					if (bestKey is null || kvp.Key.Length < bestKey.Length)
					{ bestKey = kvp.Key; pt = kvp.Value; }

			return bestKey is not null;
		}

		public bool TryResolveVisiblePointByTextFuzzy(string text, out ClickPoint pt)
		{
			pt = default;
			var key = Norm(text);
			if (key.Length == 0) return false;

			foreach (var kvp in VisibleTextToPoint)
				if (kvp.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase))
				{ pt = kvp.Value; return true; }

			string? bestKey = null;
			foreach (var kvp in VisibleTextToPoint)
				if (kvp.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
					if (bestKey is null || kvp.Key.Length < bestKey.Length)
					{ bestKey = kvp.Key; pt = kvp.Value; }

			return bestKey is not null;
		}


		public bool TryResolveUrlByLinkTextFuzzy(string linkText, out string url)
		{
			url = "";
			var key = Norm(linkText);
			if (key.Length == 0) return false;

			// 1) starts-with match
			foreach (var kvp in LinkTextToUrl)
			{
				if (kvp.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase))
				{
					url = kvp.Value;
					return true;
				}
			}

			// 2) contains match (prefer shortest key containing ours)
			string? bestKey = null;
			foreach (var kvp in LinkTextToUrl)
			{
				if (kvp.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
				{
					if (bestKey is null || kvp.Key.Length < bestKey.Length)
					{
						bestKey = kvp.Key;
						url = kvp.Value;
					}
				}
			}

			return bestKey is not null;
		}

		private static string Norm(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			s = s.Trim();
			while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");
			return s;
		}

		public readonly record struct ClickPoint(int X, int Y);

		public sealed class ScrollTarget
		{
			public string Code { get; init; } = ""; // S1, S2...
			public string Kind { get; init; } = ""; // document|element
			public string Fingerprint { get; init; } = "";
			public string? Selector { get; init; }
			public string? Description { get; init; }
			public int X { get; init; }
			public int Y { get; init; }
			public int W { get; init; }
			public int H { get; init; }
			public int ScrollTop { get; init; }
			public int ScrollHeight { get; init; }
			public int ClientHeight { get; init; }
			public int MaxScrollTop => Math.Max(0, ScrollHeight - ClientHeight);
		}

		public async Task UpdateLinkTableFromDomAsync()
		{
			// Pull a compact list of visible-ish elements from the DOM.
			// IMPORTANT: Off-screen pruning depends on this list being viewport-intersecting only.
			try
			{
				var items = await Page.EvaluateFunctionAsync<ClickableDomItem[]>(@"() => {
				  const norm = (s) => {
				    if (!s) return '';
				    s = String(s).replaceAll('\\n',' ').replaceAll('\\t',' ');
				    while (s.indexOf('  ') >= 0) s = s.replaceAll('  ',' ');
				    return s.trim();
				  };

				  const isVisibleAndInteractive = (el) => {
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
				    } catch { return false; }
				  };

				  const rectCenter = (el) => {
				    if (!isVisibleAndInteractive(el)) return null;
				    const r = el.getBoundingClientRect();
				    if (!r || r.width < 2 || r.height < 2) return null;

				    const vw = Math.max(0, window.innerWidth || 0);
				    const vh = Math.max(0, window.innerHeight || 0);

				    // Compute the intersection of the element's rect with the viewport.
				    const ix1 = Math.max(0, r.left);
				    const iy1 = Math.max(0, r.top);
				    const ix2 = Math.min(vw, r.right);
				    const iy2 = Math.min(vh, r.bottom);
				    if (ix2 <= ix1 || iy2 <= iy1) return null; // fully off-screen

				    // Choose center of *visible* portion (not full rect).
				    let x = Math.round(ix1 + (ix2 - ix1) / 2);
				    let y = Math.round(iy1 + (iy2 - iy1) / 2);

				    // Keep safely inside viewport.
				    x = Math.min(Math.max(x, 1), Math.max(1, vw - 2));
				    y = Math.min(Math.max(y, 1), Math.max(1, vh - 2));

				    // Hit test: allow if the hit element is within el (or el within hit).
    // NOTE: for form controls (INPUT/TEXTAREA/SELECT/BUTTON) we relax this check,
    // because sites sometimes overlay pseudo-elements that intercept elementFromPoint.
    try {
      const hit = document.elementFromPoint(x, y);
      if (hit && !(el === hit || el.contains(hit) || hit.contains(el))) {
        const tg = String(el.tagName || '').toUpperCase();
        const isForm = (tg === 'INPUT' || tg === 'TEXTAREA' || tg === 'SELECT' || tg === 'BUTTON');
        if (!isForm) return null;
      }
    } catch {}

				    // Convert viewport -> document coords
				    const dx = Math.max(0, Math.round(x + (window.scrollX || 0)));
				    const dy = Math.max(0, Math.round(y + (window.scrollY || 0)));
				    return { x: dx, y: dy };
				  };

const stripStar = (s) => {
  // keep for compatibility with earlier edits; currently does NOT strip '*'
  // (the AX snapshot includes required markers like '*', so we keep them for matching)
  return norm(s);
};

const tableLabelFor = (el) => {
  try {
    // climb to containing td/th
    let cell = el;
    while (cell && cell.nodeType === 1) {
      const t = String(cell.tagName || '').toLowerCase();
      if (t === 'td' || t === 'th') break;
      cell = cell.parentElement;
    }
    if (!cell) return '';

    // 1) if td has fname, sometimes that corresponds to a label elsewhere
    // but in your markup it's ""YourName"" (camel), not ""Your Name *"", so not directly useful.

    // 2) Look at previous cell(s) in the same row for visible text
    const row = cell.parentElement;
    if (!row) return '';

    let sib = cell.previousElementSibling;
    while (sib) {
      const txt = stripStar(sib.innerText);
      if (txt) return txt;
      sib = sib.previousElementSibling;
    }

    // 3) If nothing to the left, sometimes label is above: look at the row above same column index
    const colIndex = Array.prototype.indexOf.call(row.children, cell);
    const tbody = row.parentElement;
    if (tbody) {
      const rows = Array.from(tbody.children).filter(r => r && r.tagName && String(r.tagName).toLowerCase() === 'tr');
      const rowIndex = rows.indexOf(row);
      if (rowIndex > 0) {
        const prevRow = rows[rowIndex - 1];
        const aboveCell = prevRow && prevRow.children && prevRow.children[colIndex];
        if (aboveCell) {
          const txt2 = stripStar(aboveCell.innerText);
          if (txt2) return txt2;
        }
      }
    }
  } catch {}
  return '';
};

const labelForAttr = (el) => {
  try {
    const id = el && el.id ? String(el.id) : '';
    if (!id) return '';
    const lab = document.querySelector(`label[for=""${CSS.escape(id)}""]`);
    if (!lab) return '';
    return stripStar(lab.innerText);
  } catch {}
  return '';
};

					const label = (el, fallback) => {
  try {
    const tag = (el && el.tagName ? String(el.tagName).toLowerCase() : '');

    const isTextbox = (el) => {
      const t = (el && el.getAttribute ? String(el.getAttribute('type') || 'text').toLowerCase() : 'text');
      // Treat these as ""textboxes"" (i.e., user-typed fields)
      return (
        tag === 'textarea' ||
        (tag === 'input' && (
          t === '' || t === 'text' || t === 'search' || t === 'email' || t === 'password' ||
          t === 'url' || t === 'tel' || t === 'number' || t === 'date' || t === 'datetime-local' ||
          t === 'month' || t === 'week' || t === 'time'
        ))
      );
    };

    const withSuffix = (base, el) => {
      base = norm(base);
      if (!base) return base;

      const id = norm(el && el.id);
      const nm = norm(el && el.getAttribute ? el.getAttribute('name') : '');

      // Prefer id, else name
      const suffix = id ? `(#${id})` : (nm ? `(${nm})` : '');
      if (!suffix) return base;

      // Avoid double appends
      if (base.endsWith(' ' + suffix) || base.includes(suffix)) return base;

      return `${base} ${suffix}`;
    };

    if (tag === 'input' || tag === 'textarea' || tag === 'select') {
      // For submit/buttons, the visible text is usually the value.
      const v  = norm(el.value);
      const ph = norm(el.getAttribute('placeholder'));
      const al = norm(el.getAttribute('aria-label'));
      const ti = norm(el.title);

      // If nothing else, fall back to name/id so the control is still addressable.
      const nm = norm(el.getAttribute('name'));
      const id = norm(el.id);

      const tbl = tableLabelFor(el);
      const lab = labelForAttr(el);

      // Prefer things in roughly the order a human would expect.
      // SPECIAL CASE: submit/button inputs should primarily use their VALUE (eg. OK)

	  const type = String(el.getAttribute('type') || '').toLowerCase();

				let base = '';

				if (tag === 'input' && (type === 'submit' || type === 'button' || type === 'reset'))
				{
					// For buttons, always prioritize visible value text so AX [button] OK matches.
					base = v || al || ti || id || nm || norm(fallback) || '';
				}
				else
				{
					// Textboxes / textareas / selects
					// Prefer human-visible label sources first (to match AX), then value/placeholder/etc.
					// Finally, fall back to element id/name so we always have a stable key.
					base = tbl || lab || v || ph || al || ti || norm(fallback) || id || nm || '';
				}

				// ✅ Only add suffix for textboxes (not selects, not submit buttons, etc.)
				return isTextbox(el) ? withSuffix(base, el) : base;
			}
  } catch {}

  // Generic elements
  return norm(el.innerText) || norm(el.getAttribute('aria-label')) || norm(el.title) || norm(fallback) || '';
};

	const out = [];
				  const push = (el, href, clickable) => {
					  const pt = rectCenter(el);
					  if (!pt) return;
					  const text = label(el, href);
					  if (!text) return;
				    out.push({ text, href: href || '', x: pt.x, y: pt.y, clickable: !!clickable });
				  };

				  // Interactables
for (const a of Array.from(document.querySelectorAll('a[href]'))) {
  const raw = norm(a.getAttribute('href') || '');
  if (!raw || raw === '#') continue;

  const isJs = raw.toLowerCase().startsWith('javascript:');
	// Keep JS-href anchors as clickable points, but do not treat them as navigable URLs.
	push(a, isJs? '' : a.href, true);
}

for (const b of Array.from(document.querySelectorAll('button, input[type=button], input[type=submit], [role=button]')))
    push(b, '', true);

// Form controls (inputs, textareas, selects, combobox-like roles, contenteditable)
for (const f of Array.from(document.querySelectorAll(
  'input:not([type=hidden]):not([disabled]), textarea:not([disabled]), select:not([disabled]), [role=textbox], [role=checkbox], [role=radio], [role=switch], [role=searchbox], [role=combobox], [contenteditable=true]'
))) {
	push(f, '', true);
}

for (const c of Array.from(document.querySelectorAll('[onclick]')))
    push(c, '', true);

// Pointer-cursor elements
const all = document.getElementsByTagName('*');
const cap = Math.min(all.length, 5000);
for (let i = 0; i < cap; i++)
{
	const el = all[i];

	// Detect pointer cursor, INCLUDING cases where it only appears on :hover.
	let hasPointer = false;
	try
	{
		const cs = window.getComputedStyle(el);
		if (cs && cs.cursor === 'pointer')
		{
			hasPointer = true;
		}
		else
		{
			// Synthetic hover probe: some sites only apply cursor:pointer on :hover.
			el.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
const cs2 = window.getComputedStyle(el);
if (cs2 && cs2.cursor === 'pointer') hasPointer = true;
el.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }));
				      }
				    } catch { }

if (hasPointer) push(el, '', true);
				  }

				  // Visible text anchors (non-clickable), for pruning AX StaticText/headings/paragraphs to viewport.
				  // IMPORTANT: keep enough text so AX node.Name can match our visibility table.
				  // We cap it to avoid gigantic keys, but don't over-truncate (otherwise we miss paragraph text).
const textTags = Array.from(document.querySelectorAll(
  'h1,h2,h3,h4,h5,h6,label,p,li,dt,dd,caption,figcaption,' +
  '[role=alert],[aria-live],.errorMessage,#errorDiv'
));
for (const el of textTags) {
				    const pt = rectCenter(el);
if (!pt) continue;
let t = norm(el.innerText);
if (!t) continue;
if (t.length > 400) t = t.slice(0, 400);
				    out.push({ text: t, href: '', x: pt.x, y: pt.y, clickable: false });
				  }

				  const seen = new Set();
const uniq = [];
for (const it of out) {
	const k = it.text + '|' + it.x + '|' + it.y;
	if (seen.has(k)) continue;
	seen.add(k);
	uniq.push(it);
	if (uniq.length >= 1200) break;
}
return uniq;
				}");

				LinkTextToUrl.Clear();
ClickTextToPoint.Clear();
VisibleTextToPoint.Clear();
if (items is null) return;

foreach (var it in items)
{
	if (string.IsNullOrWhiteSpace(it.Text))
		continue;

	var key = Norm(it.Text);
	if (key.Length == 0) continue;

	// Decide which scroll region this point belongs to (used to disambiguate duplicate text).
	var vx = it.X - WindowScrollX;
	var vy = it.Y - WindowScrollY;
	var region = ResolveScrollTargetForViewportPoint(vx, vy);
	long score = 1_000_000_000; // default: document / unknown
	if (!string.IsNullOrWhiteSpace(region) && ScrollCodeToTarget.TryGetValue(region, out var rt) && !rt.Kind.Equals("document", StringComparison.OrdinalIgnoreCase))
	{
		score = Math.Max(1, (long)rt.W * (long)rt.H);
	}

	// Always store for visibility pruning (but prefer the most specific region when text duplicates).
	if (!VisibleTextToPoint.TryGetValue(key, out var existingV) || !VisibleTextToPointScore.TryGetValue(key, out var existingVScore) || score < existingVScore)
	{
		VisibleTextToPoint[key] = new ClickPoint(it.X, it.Y);
		VisibleTextToPointScore[key] = score;
	}

	// Only store as clickable if the DOM probe marked it clickable (same duplicate-handling rule).
	if (it.Clickable)
	{
		if (!ClickTextToPoint.TryGetValue(key, out var existingC) || !ClickTextToPointScore.TryGetValue(key, out var existingCScore) || score < existingCScore)
		{
			ClickTextToPoint[key] = new ClickPoint(it.X, it.Y);
			ClickTextToPointScore[key] = score;
		}
	}

	if (!string.IsNullOrWhiteSpace(it.Href))
	{
		LinkTextToUrl.TryAdd(key, it.Href);
		GetOrAddLinkCode(it.Href);
	}
}
			}
			catch
			{
	// Best-effort; snapshot rendering should still work without URLs.
}
		}

		public async Task UpdateScrollTargetsFromDomAsync()
		{
			// Stage 1 (debug mode): collect *all* elements that explicitly advertise vertical scrolling
			// via CSS overflow-y or overflow (auto|scroll|overlay). We will re-introduce stricter checks
			// (visibility, viewport intersection, min sizes, actual scrollHeight>clientHeight, etc.) later.
			try
			{
				var items = await Page.EvaluateFunctionAsync<ScrollDomItem[]>(@"() => {
					const norm = (s) => {
						if (!s) return '';
						return String(s).replace(/\s+/g, ' ').trim();
					};

					const vw = Math.max(0, window.innerWidth || 0);
					const vh = Math.max(0, window.innerHeight || 0);

					const getDesc = (el) => {
						const id = el.id ? ('#' + el.id) : '';
						let cls = el.className ? String(el.className) : '';
						cls = cls.replace(/\s+/g, ' ').trim();
						cls = cls ? ('.' + cls.split(' ').filter(Boolean).slice(0, 3).join('.')) : '';
						let label = norm(el.getAttribute('aria-label') || el.title || '');
						if (label.length > 80) label = label.slice(0, 80);
						return el.tagName + id + cls + (label ? (' | ' + label) : '');
					};

					const selectorHint = (el) => {
						if (el.id) return '#' + el.id;
						let cls = el.className ? String(el.className) : '';
						cls = cls.replace(/\s+/g, ' ').trim();
						const c = cls ? '.' + cls.split(' ').filter(Boolean).slice(0,2).join('.') : '';
						return el.tagName.toLowerCase() + c;
					};

					const isOverflowScrollY = (cs) => {
						if (!cs) return false;
						const oy = String(cs.overflowY || '').toLowerCase();
						const o = String(cs.overflow || '').toLowerCase();
						const ok = (v) => (v === 'auto' || v === 'scroll' || v === 'overlay');
						return ok(oy) || ok(o);
					};

					const out = [];

					// Always include the document scroller
					try {
						const se = document.scrollingElement || document.documentElement;
						const st = Math.round(se.scrollTop || window.scrollY || 0);
						const sh = Math.round(se.scrollHeight || document.documentElement.scrollHeight || 0);
						const ch = Math.round(se.clientHeight || window.innerHeight || 0);
						out.push({
							kind: 'document',
							fingerprint: 'document',
							selector: '',
							description: 'document',
							x: 0, y: 0, w: vw, h: vh,
							scrollTop: st,
							scrollHeight: sh,
							clientHeight: ch,
							winScrollX: Math.round(window.scrollX || 0),
							winScrollY: Math.round(window.scrollY || 0)
						});
					} catch {}

					const all = document.getElementsByTagName('*');
					const cap = Math.min(all.length, 12000);
					for (let i = 0; i < cap; i++) {
						const el = all[i];
						if (!el || el.nodeType !== 1) continue;

						let cs = null;
						try { cs = window.getComputedStyle(el); } catch { cs = null; }
						if (!isOverflowScrollY(cs)) continue;

						let r = null;
						try { r = el.getBoundingClientRect(); } catch { r = null; }
						if (!r) continue;

						const ch = Math.round(el.clientHeight || 0);
						const sh = Math.round(el.scrollHeight || 0);
						const st = Math.round(el.scrollTop || 0);

						const left = Math.round(r.left || 0);
						const top  = Math.round(r.top  || 0);
						const w    = Math.round(r.width || 0);
						const h    = Math.round(r.height|| 0);

						// Fingerprint: keep it stable-ish, include tag/id/classes + approximate rect.
						const fp = getDesc(el) + ' @' + left + ',' + top + ',' + w + 'x' + h;

						out.push({
							kind: 'element',
							fingerprint: fp,
							selector: selectorHint(el),
							description: getDesc(el),
							x: Math.round(Math.max(0, left)),
							y: Math.round(Math.max(0, top)),
							w: Math.round(Math.max(0, w)),
							h: Math.round(Math.max(0, h)),
							scrollTop: st,
							scrollHeight: sh,
							clientHeight: ch,
							winScrollX: Math.round(window.scrollX || 0),
							winScrollY: Math.round(window.scrollY || 0)
						});
					}

					// De-dupe by fingerprint
					const seen = new Set();
					const uniq = [];
					for (const it of out) {
						if (!it || !it.fingerprint) continue;
						if (seen.has(it.fingerprint)) continue;
						seen.add(it.fingerprint);
						uniq.push(it);
						if (uniq.length >= 120) break;
					}
					return uniq;
				}");

				ScrollCodeToTarget.Clear();
				if (items is null) return;

				foreach (var it in items)
				{
					if (string.IsNullOrWhiteSpace(it.Fingerprint))
						continue;

					if (!ScrollFingerprintToCode.TryGetValue(it.Fingerprint, out var code))
					{
						code = $"S{NextScrollId++}";
						ScrollFingerprintToCode[it.Fingerprint] = code;
					}

					ScrollCodeToTarget[code] = new ScrollTarget
					{
						Code = code,
						Kind = it.Kind ?? "",
						Fingerprint = it.Fingerprint,
						Selector = string.IsNullOrWhiteSpace(it.Selector) ? null : it.Selector,
						Description = string.IsNullOrWhiteSpace(it.Description) ? null : it.Description,
						X = it.X,
						Y = it.Y,
						W = it.W,
						H = it.H,
						ScrollTop = it.ScrollTop,
						ScrollHeight = it.ScrollHeight,
						ClientHeight = it.ClientHeight
					};

					// Cache window scroll offsets for grouping.
					if ((it.Kind ?? "").Equals("document", StringComparison.OrdinalIgnoreCase))
					{
						WindowScrollX = it.WinScrollX;
						WindowScrollY = it.WinScrollY;
					}
				}
			}
			catch (Exception ex)
			{
				// Navigation races can destroy execution context mid-eval; this is transient.
				if (IsExecutionContextDestroyedError(ex))
					return;

				// Best-effort; scroll support is optional.
				Console.WriteLine($"warn: UpdateScrollTargets skipped ({ex.GetType().Name}: {ex.Message})");
			}
		}

		private static bool IsExecutionContextDestroyedError(Exception ex)
		{
			for (var cur = ex; cur is not null; cur = cur.InnerException!)
			{
				var msg = cur.Message ?? string.Empty;
				if (msg.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}
		

		private sealed class ClickableDomItem
		{
			public string Text { get; set; } = "";
			public string Href { get; set; } = "";
			public int X { get; set; }
			public int Y { get; set; }
			public bool Clickable { get; set; }
		}

		internal sealed class ScrollDomItem
		{
			public string? Kind { get; set; }
			public string Fingerprint { get; set; } = "";
			public string? Selector { get; set; }
			public string? Description { get; set; }
			public int X { get; set; }
			public int Y { get; set; }
			public int W { get; set; }
			public int H { get; set; }
			public int ScrollTop { get; set; }
			public int ScrollHeight { get; set; }
			public int ClientHeight { get; set; }
			public int WinScrollX { get; set; }
			public int WinScrollY { get; set; }
		}

		private sealed record CookieJar
		(
			CookieJarCookie[] Cookies
		);

		private sealed record CookieJarCookie
		{
			public string? Name { get; init; }
			public string? Value { get; init; }
			public string? Domain { get; init; }
			public string? Path { get; init; }
			public double? Expires { get; init; }
			public bool? HttpOnly { get; init; }
			public bool? Secure { get; init; }
			public string? SameSite { get; init; }
			public string? Url { get; init; }
		}

		// ---- Cookie API compatibility helpers (PuppeteerSharp versions vary) ----
		private static async Task<IReadOnlyList<object>> GetCookiesCompatAsync(BrowserContext ctx)
		{
			var t = ctx.GetType();
			var m = t.GetMethod("CookiesAsync", Type.EmptyTypes)
				?? t.GetMethod("GetCookiesAsync", Type.EmptyTypes);
			if (m is not null)
			{
				var taskObj = m.Invoke(ctx, null);
				if (taskObj is Task task)
				{
					await task.ConfigureAwait(false);
					var resultProp = task.GetType().GetProperty("Result");
					if (resultProp?.GetValue(taskObj) is System.Collections.IEnumerable en)
					{
						var list = new List<object>();
						foreach (var it in en) if (it is not null) list.Add(it);
						return list;
					}
				}
			}

			// Fallback: CDP
			var page = await ctx.NewPageAsync();
			try
			{
				var client = await page.Target.CreateCDPSessionAsync();
				var res = await client.SendAsync("Network.getAllCookies");
				var elem = res is JsonElement je ? je : JsonSerializer.SerializeToElement(res);
				if (elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty("cookies", out var arr) && arr.ValueKind == JsonValueKind.Array)
				{
					var list = new List<object>();
					foreach (var c in arr.EnumerateArray()) list.Add(c);
					return list;
				}
			}
			finally
			{
				try { await page.CloseAsync(); } catch { }
			}
			return Array.Empty<object>();
		}

		private static async Task SetCookiesCompatAsync(BrowserContext ctx, CookieParam[] cookies)
		{
			var t = ctx.GetType();
			var m = t.GetMethod("SetCookiesAsync", new[] { typeof(CookieParam[]) })
				?? t.GetMethod("SetCookieAsync", new[] { typeof(CookieParam[]) });
			if (m is not null)
			{
				var taskObj = m.Invoke(ctx, new object[] { cookies });
				if (taskObj is Task task)
				{
					await task.ConfigureAwait(false);
					return;
				}
			}

			// Fallback: CDP
			var page = await ctx.NewPageAsync();
			try
			{
				var client = await page.Target.CreateCDPSessionAsync();
				foreach (var c in cookies)
				{
					var payload = new Dictionary<string, object?>
					{
						["name"] = c.Name,
						["value"] = c.Value,
						["secure"] = c.Secure,
						["httpOnly"] = c.HttpOnly,
					};

					// CDP is picky: only include fields when they are valid.
					// Prefer url when present; otherwise fall back to domain + path.
					if (!string.IsNullOrWhiteSpace(c.Url))
					{
						payload["url"] = c.Url;
					}
					else if (!string.IsNullOrWhiteSpace(c.Domain))
					{
						payload["domain"] = c.Domain;
						payload["path"] = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path;
					}
					else
					{
						// Can't place a cookie without either url or domain.
						continue;
					}

					if (c.Expires.HasValue) payload["expires"] = c.Expires.Value;
					if (c.SameSite.HasValue) payload["sameSite"] = c.SameSite.Value.ToString();

					await client.SendAsync("Network.setCookie", payload);
				}
			}
			finally
			{
				try { await page.CloseAsync(); } catch { }
			}
		}

		internal static async Task PersistCookieJarAsync(BrowserContext context, string? profileId)
		{
			// PuppeteerSharp has changed cookie APIs across versions.
			// We use reflection so this project builds against multiple versions.
			static T? GetProp<T>(object obj, string name)
			{
				var p = obj.GetType().GetProperty(name);
				if (p is null) return default;
				var v = p.GetValue(obj);
				if (v is null) return default;
				return (T)Convert.ChangeType(v, typeof(T));
			}

			static async Task<IReadOnlyList<object>> GetCookiesCompatAsync(BrowserContext ctx)
			{
				var t = ctx.GetType();
				var m = t.GetMethod("CookiesAsync", Type.EmptyTypes)
					?? t.GetMethod("GetCookiesAsync", Type.EmptyTypes);
				if (m is not null)
				{
					var taskObj = m.Invoke(ctx, null);
					if (taskObj is Task task)
					{
						await task.ConfigureAwait(false);
						var resultProp = task.GetType().GetProperty("Result");
						if (resultProp?.GetValue(taskObj) is System.Collections.IEnumerable en)
						{
							var list = new List<object>();
							foreach (var it in en) if (it is not null) list.Add(it);
							return list;
						}
					}
				}

				// Fallback: use CDP to get cookies.
				var page = await ctx.NewPageAsync();
				try
				{
					var client = await page.Target.CreateCDPSessionAsync();
					var res = await client.SendAsync("Network.getAllCookies");
					var elem = res is JsonElement je ? je : JsonSerializer.SerializeToElement(res);
					if (elem.ValueKind == JsonValueKind.Object && elem.TryGetProperty("cookies", out var arr) && arr.ValueKind == JsonValueKind.Array)
					{
						var list = new List<object>();
						foreach (var c in arr.EnumerateArray()) list.Add(c);
						return list;
					}
				}
				finally
				{
					try { await page.CloseAsync(); } catch { }
				}
				return Array.Empty<object>();
			}

			static async Task SetCookiesCompatAsync(BrowserContext ctx, CookieParam[] cookies)
			{
				var t = ctx.GetType();
				var m = t.GetMethod("SetCookiesAsync", new[] { typeof(CookieParam[]) })
					?? t.GetMethod("SetCookieAsync", new[] { typeof(CookieParam[]) });
				if (m is not null)
				{
					var taskObj = m.Invoke(ctx, new object[] { cookies });
					if (taskObj is Task task)
					{
						await task.ConfigureAwait(false);
						return;
					}
				}

				// Fallback via CDP: set cookies one by one.
				var page = await ctx.NewPageAsync();
				try
				{
					var client = await page.Target.CreateCDPSessionAsync();
					foreach (var c in cookies)
					{
						var payload = new Dictionary<string, object?>
						{
							["name"] = c.Name,
							["value"] = c.Value,
							["secure"] = c.Secure,
							["httpOnly"] = c.HttpOnly,
						};

						// CDP is picky: only include fields when they are valid.
						// Prefer url when present; otherwise fall back to domain + path.
						if (!string.IsNullOrWhiteSpace(c.Url))
						{
							payload["url"] = c.Url;
						}
						else if (!string.IsNullOrWhiteSpace(c.Domain))
						{
							payload["domain"] = c.Domain;
							payload["path"] = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path;
						}
						else
						{
							// Can't place a cookie without either url or domain.
							continue;
						}

						if (c.Expires.HasValue) payload["expires"] = c.Expires.Value;
						if (c.SameSite.HasValue) payload["sameSite"] = c.SameSite.Value.ToString();

						await client.SendAsync("Network.setCookie", payload);
					}
				}
				finally
				{
					try { await page.CloseAsync(); } catch { }
				}
			}

			var cookieJarPath = GetCookieJarPath(profileId);
			Directory.CreateDirectory(Path.GetDirectoryName(cookieJarPath) ?? AppContext.BaseDirectory);

			var cookies = await GetCookiesCompatAsync(context);
			var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

			var jarCookies = cookies
				.Where(c => c is not null)
				.Select(c =>
				{
					// Cookies can come from PuppeteerSharp strongly-typed cookie objects OR from CDP fallback as JsonElement.
					string? name = null, value = null, domain = null, path = null, sameSite = null, url = null;
					double? expires = null;
					bool? httpOnly = null, secure = null;

					if (c is JsonElement je)
					{
						name = GetString(je, "name");
						value = GetString(je, "value");
						domain = GetString(je, "domain");
						path = GetString(je, "path");
						url = GetString(je, "url");

						if (je.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number && ex.TryGetDouble(out var exd))
							expires = exd;
						if (je.TryGetProperty("httpOnly", out var ho) && (ho.ValueKind == JsonValueKind.True || ho.ValueKind == JsonValueKind.False))
							httpOnly = ho.GetBoolean();
						if (je.TryGetProperty("secure", out var se) && (se.ValueKind == JsonValueKind.True || se.ValueKind == JsonValueKind.False))
							secure = se.GetBoolean();
						if (je.TryGetProperty("sameSite", out var ss) && ss.ValueKind == JsonValueKind.String)
							sameSite = ss.GetString();
					}
					else
					{
						// Reflection against PuppeteerSharp's cookie type(s)
						static T? R<T>(object obj, string prop)
						{
							var p = obj.GetType().GetProperty(prop);
							if (p is null) return default;
							var v = p.GetValue(obj);
							if (v is null) return default;
							try { return (T)Convert.ChangeType(v, typeof(T)); } catch { return (T?)v; }
						}

						name = R<string>(c, "Name");
						value = R<string>(c, "Value");
						domain = R<string>(c, "Domain");
						path = R<string>(c, "Path");
						expires = R<double?>(c, "Expires");
						httpOnly = R<bool?>(c, "HttpOnly");
						secure = R<bool?>(c, "Secure");
						// Some versions expose SameSite as enum; some as string.
						var ssObj = c.GetType().GetProperty("SameSite")?.GetValue(c);
						sameSite = ssObj?.ToString();
						url = R<string>(c, "Url");
					}

					return new CookieJarCookie
					{
						Name = name,
						Value = value,
						Domain = domain,
						Path = path,
						Expires = expires,
						HttpOnly = httpOnly,
						Secure = secure,
						SameSite = sameSite,
						Url = url
					};
				})
				// Drop obviously expired cookies (keeps the jar smaller / less weird).
				.Where(c => c.Expires is null || c.Expires <= 0 || c.Expires > nowUnix)
				.ToArray();

			var jar = new CookieJar(jarCookies);
			var json = JsonSerializer.Serialize(jar, CookieJarJson);
			await File.WriteAllTextAsync(cookieJarPath, json);
		}

		internal static async Task RestoreCookieJarAsync(BrowserContext context, string? profileId)
		{
			var cookieJarPath = GetCookieJarPath(profileId);
			if (!File.Exists(cookieJarPath)) return;

			CookieJar? jar;
			try
			{
				var json = await File.ReadAllTextAsync(cookieJarPath);
				jar = JsonSerializer.Deserialize<CookieJar>(json, CookieJarJson);
			}
			catch
			{
				return;
			}

			if (jar?.Cookies is null || jar.Cookies.Length == 0) return;

			var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			var cookieParams = new List<CookieParam>(jar.Cookies.Length);
			foreach (var c in jar.Cookies)
			{
				if (string.IsNullOrWhiteSpace(c.Name)) continue;
				if (c.Expires is not null && c.Expires > 0 && c.Expires <= nowUnix) continue; // expired

				var p = new CookieParam
				{
					Name = c.Name,
					Value = c.Value ?? string.Empty,
					Domain = string.IsNullOrWhiteSpace(c.Domain) ? null : c.Domain,
					Path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path,
					Expires = c.Expires,
					HttpOnly = c.HttpOnly ?? false,
					Secure = c.Secure ?? false,
					Url = string.IsNullOrWhiteSpace(c.Url) ? null : c.Url
				};

				// SameSite is optional; only set when parseable.
				if (!string.IsNullOrWhiteSpace(c.SameSite)
					&& Enum.TryParse<SameSite>(c.SameSite, ignoreCase: true, out var ss))
				{
					p.SameSite = ss;
				}

				cookieParams.Add(p);
			}

			if (cookieParams.Count == 0) return;

			// PuppeteerSharp will apply cookies to the context store.
			await SetCookiesCompatAsync(context, cookieParams.ToArray());
		}
	}

	private sealed class BrowserLaunchConfig
	{
		public string? ChromiumPath { get; init; }
		public bool AllowDownloadChromium { get; init; }

		// Timeouts (ms)
		public int LaunchTimeoutMs { get; init; } = 60_000;
		public int DefaultTimeoutMs { get; init; } = 60_000;
		public int NavigationTimeoutMs { get; init; } = 60_000;

		// CDP protocol call timeout (ms)
		public int ProtocolTimeoutMs { get; init; } = 180_000;

		// Retry for flaky CDP attach timeouts.
		public int NavigateMaxAttempts { get; init; } = 2;

		public IReadOnlyList<string> ExtraChromiumArgs { get; init; } = [];

		public static BrowserLaunchConfig FromEnvironment()
		{
			static int GetInt(string name, int fallback)
				=> int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

			static bool GetBool(string name, bool fallback)
				=> bool.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

			var allowDownload = GetBool("CFAGENT_ALLOW_DOWNLOAD_CHROMIUM", true);

			var chromiumPath = Environment.GetEnvironmentVariable("CFAGENT_CHROMIUM_PATH");

			var extraArgs = Environment.GetEnvironmentVariable("CFAGENT_EXTRA_CHROMIUM_ARGS")
				?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.ToArray() ?? [];

			return new BrowserLaunchConfig
			{
				ChromiumPath = chromiumPath,
				AllowDownloadChromium = allowDownload,
				LaunchTimeoutMs = GetInt("CFAGENT_LAUNCH_TIMEOUT_MS", 60_000),
				DefaultTimeoutMs = GetInt("CFAGENT_DEFAULT_TIMEOUT_MS", 60_000),
				NavigationTimeoutMs = GetInt("CFAGENT_NAV_TIMEOUT_MS", 60_000),
				ProtocolTimeoutMs = GetInt("CFAGENT_PROTOCOL_TIMEOUT_MS", 180_000),
				NavigateMaxAttempts = GetInt("CFAGENT_NAV_MAX_ATTEMPTS", 2),
				ExtraChromiumArgs = extraArgs
			};
		}
	}

	private sealed class CommandContextScope(Guid? priorSessionId, TextWriter? priorWriter) : IDisposable
	{
		public void Dispose()
		{
			RoutedSessionId.Value = priorSessionId;
			RoutedOutput.Value = priorWriter;
		}
	}

	private sealed class RoutedConsoleWriter : TextWriter
	{
		public override Encoding Encoding => StandardOutput.Encoding;

		private static TextWriter Current => RoutedOutput.Value ?? StandardOutput;

		public override void Flush() => Current.Flush();
		public override void Write(char value) => Current.Write(value);
		public override void Write(string? value) => Current.Write(value);
		public override void WriteLine() => Current.WriteLine();
		public override void WriteLine(string? value) => Current.WriteLine(value);
	}
}




















