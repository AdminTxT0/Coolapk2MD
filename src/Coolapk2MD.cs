// Coolapk2MD —— 酷安图文/动态 -> 本地 Markdown + 图片
// 目标框架 .NET Framework 4.0+（Win10/11 自带 4.8，无需另装运行时）
// 单文件 exe，约 50KB。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Coolapk2MD
{
    // ============================================================ 迷你 DOM

    sealed class Node
    {
        public string Tag;
        public Dictionary<string, string> Attrs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<object> Children = new List<object>();
        public Node Parent;

        public string Cls
        {
            get { string v; return Attrs.TryGetValue("class", out v) && v != null ? v : ""; }
        }

        public bool HasCls(string name)
        {
            if (name.Length == 0) return false;
            string c = Cls;
            if (c.IndexOf(name, StringComparison.Ordinal) < 0) return false;
            string[] parts = c.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                if (parts[i] == name) return true;
            return false;
        }

        public string Attr(string key)
        {
            string v;
            return Attrs.TryGetValue(key, out v) && v != null ? v : "";
        }
    }

    static class HtmlParser
    {
        static readonly HashSet<string> VoidTags = new HashSet<string>
        {
            "area","base","br","col","embed","hr","img","input","link","meta","param","source","track","wbr"
        };

        static bool At(string s, int i, string what)
        {
            return i + what.Length <= s.Length && string.CompareOrdinal(s, i, what, 0, what.Length) == 0;
        }

        public static Node Parse(string html)
        {
            Node root = new Node();
            root.Tag = "#document";
            List<Node> stack = new List<Node>();
            stack.Add(root);

            int i = 0, n = html.Length;
            while (i < n)
            {
                char ch = html[i];
                if (ch != '<')
                {
                    int next = html.IndexOf('<', i);
                    if (next < 0) next = n;
                    string text = html.Substring(i, next - i);
                    if (text.Length > 0)
                        stack[stack.Count - 1].Children.Add(WebUtility.HtmlDecode(text));
                    i = next;
                    continue;
                }

                if (At(html, i, "<!--"))
                {
                    int e = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = e < 0 ? n : e + 3;
                    continue;
                }
                if (At(html, i, "<!"))
                {
                    int e = html.IndexOf('>', i);
                    i = e < 0 ? n : e + 1;
                    continue;
                }
                if (At(html, i, "</"))
                {
                    int e = html.IndexOf('>', i);
                    string name = (e < 0 ? html.Substring(i + 2) : html.Substring(i + 2, e - i - 2)).Trim();
                    name = name.ToLowerInvariant();
                    for (int k = stack.Count - 1; k > 0; k--)
                    {
                        if (stack[k].Tag == name)
                        {
                            stack.RemoveRange(k, stack.Count - k);
                            break;
                        }
                    }
                    i = e < 0 ? n : e + 1;
                    continue;
                }

                if (i + 1 < n && char.IsLetter(html[i + 1]))
                {
                    int p = i + 1;
                    int nameEnd = p;
                    while (nameEnd < n && !char.IsWhiteSpace(html[nameEnd]) && html[nameEnd] != '>' && html[nameEnd] != '/')
                        nameEnd++;
                    string tag = html.Substring(p, nameEnd - p).ToLowerInvariant();

                    Dictionary<string, string> attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    p = nameEnd;
                    bool selfClose = false;

                    while (p < n)
                    {
                        while (p < n && char.IsWhiteSpace(html[p])) p++;
                        if (p >= n) break;
                        if (html[p] == '>') { p++; break; }
                        if (html[p] == '/')
                        {
                            if (p + 1 < n && html[p + 1] == '>') { selfClose = true; p += 2; break; }
                            p++;
                            continue;
                        }

                        int an = p;
                        while (an < n && html[an] != '=' && html[an] != '>' && !char.IsWhiteSpace(html[an]) && html[an] != '/')
                            an++;
                        if (an == p) { p++; continue; }
                        string attrName = html.Substring(p, an - p);
                        p = an;
                        while (p < n && char.IsWhiteSpace(html[p])) p++;

                        string attrVal = "";
                        if (p < n && html[p] == '=')
                        {
                            p++;
                            while (p < n && char.IsWhiteSpace(html[p])) p++;
                            if (p < n && (html[p] == '"' || html[p] == '\''))
                            {
                                char quote = html[p];
                                p++;
                                int vs = p;
                                while (p < n && html[p] != quote) p++;
                                attrVal = html.Substring(vs, p - vs);
                                if (p < n) p++;
                            }
                            else
                            {
                                int vs = p;
                                while (p < n && !char.IsWhiteSpace(html[p]) && html[p] != '>') p++;
                                attrVal = html.Substring(vs, p - vs);
                            }
                        }
                        attrs[attrName] = WebUtility.HtmlDecode(attrVal);
                    }

                    Node node = new Node();
                    node.Tag = tag;
                    node.Attrs = attrs;
                    node.Parent = stack[stack.Count - 1];
                    stack[stack.Count - 1].Children.Add(node);
                    if (!selfClose && !VoidTags.Contains(tag))
                        stack.Add(node);
                    i = p;
                    continue;
                }

                stack[stack.Count - 1].Children.Add("<");
                i++;
            }
            return root;
        }

        public static IEnumerable<Node> Walk(Node root)
        {
            Stack<Node> st = new Stack<Node>();
            st.Push(root);
            while (st.Count > 0)
            {
                Node cur = st.Pop();
                for (int i = 0; i < cur.Children.Count; i++)
                {
                    Node c = cur.Children[i] as Node;
                    if (c != null) { yield return c; st.Push(c); }
                }
            }
        }

        public static List<Node> FindAll(Node root, Func<Node, bool> pred)
        {
            List<Node> list = new List<Node>();
            foreach (Node n in Walk(root)) if (pred(n)) list.Add(n);
            return list;
        }

        public static Node FindFirst(Node root, Func<Node, bool> pred)
        {
            foreach (Node n in Walk(root)) if (pred(n)) return n;
            return null;
        }
    }

    // ============================================================ 转换上下文

    sealed class Ctx
    {
        public string OutDir;
        public int ImgNo;
        public List<string> Saved = new List<string>();
        public List<string> Failed = new List<string>();
        HashSet<string> used = new HashSet<string>();

        static readonly Regex Emoticon = new Regex(@"^https?://static\.coolapk\.com/emoticons/", RegexOptions.IgnoreCase);

        public string DownloadImage(string url, Action<string> fetch)
        {
            ImgNo++;
            int idx = ImgNo;
            string ext = GuessExt(url);
            string name = string.Format("{0:00}{1}", idx, ext);
            for (int k = 0; k < 100; k++)
            {
                string cand = k == 0 ? name : string.Format("{0:00}_{1}{2}", idx, k, ext);
                string rel = "images/" + cand;
                if (!used.Contains(rel) && !File.Exists(Path.Combine(OutDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                {
                    used.Add(rel);
                    name = cand;
                    break;
                }
            }
            string abs = Path.Combine(OutDir, "images", name);
            byte[] data = null;
            try { data = Http.GetBytes(url); }
            catch (Exception) { data = null; }

            if (data == null || data.Length == 0)
            {
                Failed.Add(url);
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            File.WriteAllBytes(abs, data);

            string realExt = MagicExt(data);
            if (realExt != null && !name.EndsWith(realExt, StringComparison.OrdinalIgnoreCase))
            {
                string renamed = Path.GetFileNameWithoutExtension(name) + realExt;
                string newAbs = Path.Combine(OutDir, "images", renamed);
                File.Move(abs, newAbs);
                abs = newAbs;
                name = renamed;
            }
            Saved.Add(url);
            return "images/" + name;
        }

        static string GuessExt(string url)
        {
            string path = url;
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            path = Regex.Replace(path, @"\.m\.(jpe?g|png|webp|gif)$", ".$1", RegexOptions.IgnoreCase).ToLowerInvariant();
            Match m = Regex.Match(path, @"\.(jpe?g|png|webp|gif|bmp)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string e = m.Groups[1].Value.ToLowerInvariant();
                return e == "jpeg" ? ".jpg" : "." + e;
            }
            return ".jpg";
        }

        static string MagicExt(byte[] d)
        {
            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ".jpg";
            if (d.Length >= 4 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return ".png";
            if (d.Length >= 4 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return ".gif";
            if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F'
                && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return ".webp";
            return null;
        }

        public static bool IsEmoticon(string url)
        {
            return Emoticon.IsMatch(url);
        }
    }

    // ============================================================ HTTP

    static class Http
    {
        public const string UA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        static Http()
        {
            try
            {
                // Tls12(3072) | Tls13(12288)
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)16128;
            }
            catch (Exception) { }
            ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.Expect100Continue = false;
        }

        public static byte[] GetBytes(string url, int retries = 3)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = UA;
                    req.Referer = "https://www.coolapk.com/";
                    req.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
                    req.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 30000;
                    req.AllowAutoRedirect = true;
                    try { req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; }
                    catch (Exception) { }

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[16384];
                        int r;
                        while ((r = s.Read(buf, 0, buf.Length)) > 0)
                            ms.Write(buf, 0, r);
                        return ms.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < retries) Thread.Sleep(1000 * attempt);
                }
            }
            throw new Exception("网络错误：" + (last == null ? "未知" : last.Message));
        }
    }

    // ============================================================ 转换器

    sealed class ConvertResult
    {
        public bool Ok;
        public string MdPath = "";
        public int Images;
        public int FailedImages;
        public string Message = "";
    }

    sealed class FeedConverter
    {
        static readonly HashSet<string> BlockTags = new HashSet<string>
        {
            "p","div","section","article","blockquote","figure","table","hr","ul","ol","li","h1","h2","h3","h4","h5","h6"
        };

        static readonly HashSet<string> InlineTags = new HashSet<string>
        {
            "a","span","b","strong","i","em","u","s","del","code","font","sub","sup","small","mark","abbr","time","cite","label"
        };

        // 源站畸形标签把属性碎片漏进正文时清理掉
        static readonly Regex GarbageAttr = new Regex(
            "\"\\s*(?:target|rel|class|title|style|href|align|width|height|id|name)\\s*=\\s*\"[^\"]*\"?\\s*>?");

        static readonly Regex GarbageAttr2 = new Regex(
            "\\s*(?:target|rel|class|title|style|href|align|width|height|id|name)\\s*=\\s*\"[^\"]*\"?\\s*>?");

        // 行首若像 Markdown 块语法，加反斜杠
        static readonly Regex LineStartEscape = new Regex(@"^([>#|]|[-*+]\s|\d+[.)]\s)");

        static readonly Regex IllegalChars = new Regex("[\\\\/:*?\"<>|\\x00-\\x1f]");

        static readonly Regex TokenRe = new Regex(@"[?&]s=([A-Za-z0-9%_\-]+)");
        static readonly Regex IdRe = new Regex(@"coolapk\.com/feed/(\d+)", RegexOptions.IgnoreCase);
        static readonly Regex BareIdRe = new Regex(@"^\d{4,}$");

        readonly Action<string> log;

        public FeedConverter(Action<string> log) { this.log = log; }

        // ---------- 入口

        public ConvertResult Convert(string raw, string outRoot)
        {
            ConvertResult result = new ConvertResult();

            string id, token;
            ParseTarget(raw, out id, out token);
            if (id == null)
            {
                result.Message = "无法识别链接里的 feed id";
                return result;
            }
            if (token == null)
            {
                result.Message = "缺少分享令牌 s。请在酷安 App 里打开该内容 -> 分享 -> 复制链接（链接会带 ?s=），再粘贴进来。";
                return result;
            }

            string url = "https://www.coolapk.com/feed/" + id + "?s=" + Uri.EscapeDataString(token);
            log("抓取 feed/" + id + " ...");

            string page;
            try
            {
                page = Encoding.UTF8.GetString(Http.GetBytes(url, 3));
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                return result;
            }

            if (page.IndexOf("请用酷安手机APP扫码", StringComparison.Ordinal) >= 0
                || page.IndexOf("酷安手机APP扫码查看详情", StringComparison.Ordinal) >= 0)
            {
                result.Message = "服务端返回了壳页：令牌失效或与这条内容不匹配。请回 App 重新分享。";
                return result;
            }

            log("解析 HTML (" + page.Length + " 字符) ...");
            Node root = HtmlParser.Parse(page);

            Node detail = HtmlParser.FindFirst(root, delegate (Node x) { return x.Attr("id") == "feed-detail"; });
            if (detail == null) detail = root;

            string title = "", author = "", date = "", device = "", cover = "";

            Node tn = HtmlParser.FindFirst(detail, delegate (Node x) { return x.HasCls("message-title"); });
            if (tn != null) title = NormWs(TextOf(tn));

            Node un = HtmlParser.FindFirst(detail, delegate (Node x) { return x.HasCls("username-item"); });
            if (un != null)
            {
                List<Node> ps = new List<Node>();
                for (int i = 0; i < un.Children.Count; i++)
                {
                    Node c = un.Children[i] as Node;
                    if (c != null && c.Tag == "p") ps.Add(c);
                }
                if (ps.Count > 0) author = NormWs(TextOf(ps[0]));
                if (ps.Count > 1)
                {
                    Node sp = HtmlParser.FindFirst(ps[1], delegate (Node x) { return x.Tag == "span"; });
                    string full = NormWs(TextOf(ps[1]));
                    if (sp != null)
                    {
                        device = NormWs(TextOf(sp));
                        full = full.Replace(device, "").Trim();
                    }
                    date = full;
                }
            }

            Node cv = HtmlParser.FindFirst(detail, delegate (Node x) { return x.HasCls("message-cover"); });
            if (cv != null)
            {
                Node cimg = HtmlParser.FindFirst(cv, delegate (Node x) { return x.Tag == "img"; });
                if (cimg != null) cover = NormUrl(cimg.Attr("src"));
            }

            Node body = ExtractBody(detail);
            if (body == null)
            {
                result.Message = "没有找到正文容器，页面结构可能已改版。";
                return result;
            }

            if (title.Length == 0)
            {
                string head = FirstLineOf(body);
                if (head.Length > 30) head = head.Substring(0, 30);
                title = head.Length > 0 ? head : ("酷安动态_" + id);
            }

            string dirName = SafeName(title);
            if (dirName.Length == 0) dirName = "feed_" + id;
            string outDir = Path.Combine(outRoot, dirName);
            if (Directory.Exists(outDir))
                outDir = Path.Combine(outRoot, dirName + "_" + id);

            Ctx ctx = new Ctx();
            ctx.OutDir = outDir;

            log("转换正文 -> " + dirName);
            string coverLocal = null;
            if (cover.Length > 0 && !Ctx.IsEmoticon(cover))
                coverLocal = ctx.DownloadImage(cover, null);

            List<string> blocks = RenderBlocks(body, ctx);
            string md = BuildMarkdown(title, author, date, device, url, coverLocal, blocks);

            Directory.CreateDirectory(outDir);
            string mdPath = Path.Combine(outDir, "index.md");
            File.WriteAllText(mdPath, md, new UTF8Encoding(false));

            result.Ok = true;
            result.MdPath = mdPath;
            result.Images = ctx.Saved.Count;
            result.FailedImages = ctx.Failed.Count;
            return result;
        }

        // ---------- 目标解析

        static void ParseTarget(string raw, out string id, out string token)
        {
            id = null;
            token = null;
            if (raw == null) return;
            raw = raw.Trim();
            if (raw.Length == 0) return;

            Match m = IdRe.Match(raw);
            if (m.Success) id = m.Groups[1].Value;
            else if (BareIdRe.IsMatch(raw)) id = raw;

            Match t = TokenRe.Match(raw);
            if (t.Success) token = Uri.UnescapeDataString(t.Groups[1].Value);
        }

        // ---------- 正文定位

        static Node ExtractBody(Node detail)
        {
            Node textNode = null;
            string[] classes = new string[] { "feed-article-message", "feed-message" };
            for (int i = 0; i < classes.Length && textNode == null; i++)
            {
                string cls = classes[i];
                textNode = HtmlParser.FindFirst(detail, delegate (Node x) { return x.HasCls(cls); });
            }
            if (textNode == null) return null;

            Node virtualNode = new Node();
            virtualNode.Tag = "div";
            virtualNode.Children.Add(textNode);

            Node parent = textNode.Parent;
            if (parent != null)
            {
                bool started = false;
                for (int i = 0; i < parent.Children.Count; i++)
                {
                    Node ch = parent.Children[i] as Node;
                    if (ch == null) continue;
                    if (object.ReferenceEquals(ch, textNode)) { started = true; continue; }
                    if (!started) continue;

                    string c = ch.Cls;
                    if (c.IndexOf("feed-status") >= 0 || c.IndexOf("feed-hot-reply") >= 0
                        || c.IndexOf("pswp") >= 0 || c.IndexOf("common-userinfo-group") >= 0
                        || c.IndexOf("message-cover") >= 0)
                        break;

                    if (c.IndexOf("message-image-group") >= 0 || c.IndexOf("message-image-item") >= 0)
                        virtualNode.Children.Add(ch);
                }
            }
            return virtualNode;
        }

        static string FirstLineOf(Node node)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                Node ch = node.Children[i] as Node;
                if (ch == null) continue;
                if (ch.Tag == "div" || ch.Tag == "section" || ch.Tag == "article")
                {
                    string t = FirstLineOf(ch);
                    if (t.Length > 0) return t;
                    continue;
                }
                if (ch.Tag != "p") continue;

                List<string> parts = new List<string>();
                for (int j = 0; j < ch.Children.Count; j++)
                {
                    object o = ch.Children[j];
                    Node c = o as Node;
                    if (c == null) { parts.Add((string)o); continue; }
                    if (c.Tag == "br") break;
                    if (InlineTags.Contains(c.Tag)) { parts.Add(TextOf(c)); continue; }
                    break;
                }
                string line = NormWs(string.Join("", parts.ToArray()));
                if (line.Length > 0) return line;
            }
            return "";
        }

        // ---------- 文本工具

        static string TextOf(object node)
        {
            string s = node as string;
            if (s != null) return s;
            Node n = (Node)node;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n.Children.Count; i++)
                sb.Append(TextOf(n.Children[i]));
            return sb.ToString();
        }

        static string NormWs(string s)
        {
            if (s == null) return "";
            s = s.Replace('\u00a0', ' ');
            s = Regex.Replace(s, "[ \t\r\n]+", " ");
            return s.Trim();
        }

        static string NormUrl(string u)
        {
            if (u == null) return "";
            u = u.Trim();
            if (u.Length == 0) return "";
            if (u.StartsWith("//")) return "https:" + u;
            if (u.StartsWith("/")) return "https://www.coolapk.com" + u;
            return u;
        }

        static string MdUrl(string u)
        {
            bool ascii = true;
            for (int i = 0; i < u.Length; i++) if (u[i] > 127) { ascii = false; break; }
            if (ascii) return u;
            try
            {
                Uri uri = new Uri(u);
                StringBuilder sb = new StringBuilder();
                sb.Append(uri.GetLeftPart(UriPartial.Scheme));
                sb.Append(uri.Host);
                string path = uri.AbsolutePath;
                sb.Append(Uri.EscapeUriString(path).Replace("?", "%3F").Replace("#", "%23"));
                if (uri.Query.Length > 1)
                    sb.Append(uri.Query.Replace(" ", "%20"));
                return sb.ToString();
            }
            catch (Exception) { return u; }
        }

        static string CleanLabel(string s)
        {
            s = GarbageAttr.Replace(s, "");
            s = GarbageAttr2.Replace(s, "");
            s = Regex.Replace(s, "\\s+", " ").Trim();
            int len = s.Length;
            if (len >= 12 && len % 2 == 0)
            {
                string half = s.Substring(0, len / 2);
                if (half == s.Substring(len / 2)) s = half.Trim();
            }
            return s;
        }

        // ---------- Markdown 渲染

        static string MdNode(object ch, Ctx ctx)
        {
            string raw = ch as string;
            if (raw != null)
                return Regex.Replace(raw.Replace('\u00a0', ' '), "[ \t\r\n]+", " ");

            Node n = (Node)ch;

            if (n.Tag == "br") return "\n";
            if (n.Tag == "img") return MdImage(n, ctx);

            if (n.Tag == "a")
            {
                string href = NormUrl(n.Attr("href"));
                string label = CleanLabel(NormWs(TextOf(n)));
                if (label.Length == 0) label = n.Attr("title");
                if (label.Length == 0) label = href;
                if (href.Length == 0) return label;
                string low = href.ToLowerInvariant();
                if (low.StartsWith("javascript:") || low.StartsWith("#")) return label;
                return "[" + label + "](" + MdUrl(href) + ")";
            }

            string inner = MdInline(n, ctx);
            if (BlockTags.Contains(n.Tag)) return "\n" + inner + "\n";
            return inner;
        }

        static string MdInline(Node node, Ctx ctx)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < node.Children.Count; i++)
                sb.Append(MdNode(node.Children[i], ctx));
            return sb.ToString();
        }

        static string MdImage(Node img, Ctx ctx)
        {
            string src = img.Attr("src");
            if (src.Length == 0) src = img.Attr("data-src");

            if (img.HasCls("feed-emoji") || Ctx.IsEmoticon(src))
            {
                string alt = img.Attr("alt");
                if (alt.Length == 0) alt = img.Attr("title");
                return alt;
            }

            src = NormUrl(src);
            if (src.Length == 0) return "";

            string rel = ctx.DownloadImage(src, null);
            string altText = "图" + ctx.ImgNo;
            if (rel != null) return "\n\n![" + altText + "](" + rel + ")\n\n";
            return "\n\n![" + altText + "](" + MdUrl(src) + ")\n\n";
        }

        static List<string> RenderBlocks(Node node, Ctx ctx)
        {
            List<string> blocks = new List<string>();
            StringBuilder buf = new StringBuilder();

            for (int i = 0; i < node.Children.Count; i++)
            {
                object o = node.Children[i];

                string raw = o as string;
                if (raw != null)
                {
                    buf.Append(Regex.Replace(WebUtility.HtmlDecode(raw).Replace('\u00a0', ' '), "[ \t\r\n]+", " "));
                    continue;
                }

                Node ch = (Node)o;
                string cls = ch.Cls;

                if (cls.IndexOf("message-image-item") >= 0 || ch.Tag == "figure")
                {
                    Flush(blocks, buf);
                    List<Node> imgs = HtmlParser.FindAll(ch, delegate (Node x) { return x.Tag == "img"; });
                    for (int k = 0; k < imgs.Count; k++)
                    {
                        string s = MdImage(imgs[k], ctx).Trim();
                        if (s.Length > 0) blocks.Add(s);
                    }
                    continue;
                }

                if (ch.Tag == "li")
                {
                    Flush(blocks, buf);
                    string inner = TidyInline(MdInline(ch, ctx));
                    if (inner.Trim().Length > 0)
                        blocks.Add("- " + inner.Replace("  \n", "  \n  "));
                    continue;
                }

                if (ch.Tag.Length == 2 && ch.Tag[0] == 'h' && ch.Tag[1] >= '1' && ch.Tag[1] <= '6')
                {
                    Flush(blocks, buf);
                    int level = ch.Tag[1] - '0' + 1;
                    if (level > 6) level = 6;
                    string t = TidyInline(MdInline(ch, ctx)).Replace("  \n", " ");
                    if (t.Trim().Length > 0)
                        blocks.Add(new string('#', level) + " " + t.Trim());
                    continue;
                }

                if (ch.Tag == "hr")
                {
                    Flush(blocks, buf);
                    blocks.Add("---");
                    continue;
                }

                if (ch.Tag == "img")
                {
                    buf.Append(MdImage(ch, ctx));
                    continue;
                }

                if (BlockTags.Contains(ch.Tag))
                {
                    Flush(blocks, buf);
                    blocks.AddRange(RenderBlocks(ch, ctx));
                    continue;
                }

                buf.Append(MdNode(ch, ctx));
            }

            Flush(blocks, buf);
            return blocks;
        }

        static void Flush(List<string> blocks, StringBuilder buf)
        {
            if (buf.Length == 0) return;
            string s = TidyInline(buf.ToString());
            buf.Length = 0;
            if (s.Trim().Length > 0) blocks.Add(s);
        }

        static string TidyInline(string s)
        {
            s = s.Replace("\r", "");
            s = Regex.Replace(s, "[ \t]+", " ");
            string[] raw = s.Split('\n');
            List<string> lines = new List<string>();
            for (int i = 0; i < raw.Length; i++)
            {
                string ln = raw[i].Trim();
                if (ln.Length == 0)
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Length != 0)
                        lines.Add("");
                    continue;
                }
                lines.Add(ln);
            }
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) return "";

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                if (lines[i].Length == 0) continue;
                sb.Append(LineStartEscape.Replace(lines[i], "\\$0"));
                sb.Append("  ");
            }
            return sb.ToString().TrimEnd(' ', '\n');
        }

        // ---------- 组装

        static string BuildMarkdown(string title, string author, string date, string device,
                                    string url, string coverLocal, List<string> blocks)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append("title: ").Append(Yaml(title)).Append('\n');
            if (author.Length > 0) sb.Append("author: ").Append(Yaml(author)).Append('\n');
            if (date.Length > 0) sb.Append("date: ").Append(Yaml(date)).Append('\n');
            if (device.Length > 0) sb.Append("device: ").Append(Yaml(device)).Append('\n');
            sb.Append("source: ").Append(Yaml(url)).Append('\n');
            sb.Append("---\n\n");
            sb.Append("# ").Append(title).Append("\n\n");

            List<string> meta = new List<string>();
            if (author.Length > 0) meta.Add("作者：" + author);
            if (date.Length > 0) meta.Add("时间：" + date);
            if (device.Length > 0) meta.Add("机型：" + device);
            if (meta.Count > 0) sb.Append("> ").Append(string.Join(" ｜ ", meta.ToArray())).Append("\n\n");
            sb.Append("> 原文：").Append(url).Append("\n\n");

            if (coverLocal != null) sb.Append("![封面](").Append(coverLocal).Append(")\n\n");

            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Trim().Length == 0) continue;
                sb.Append(blocks[i]).Append("\n\n");
            }
            return sb.ToString();
        }

        static string Yaml(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        static string SafeName(string s)
        {
            s = IllegalChars.Replace(s == null ? "" : s, "_");
            s = s.Trim().Trim('.');
            s = Regex.Replace(s, "\\s+", " ");
            if (s.Length > 60) s = s.Substring(0, 60);
            return s.Trim();
        }
    }
    // ============================================================ 视觉主题

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(243, 245, 248);
        public static readonly Color Card = Color.White;
        public static readonly Color CardBorder = Color.FromArgb(233, 236, 240);
        public static readonly Color Primary = Color.FromArgb(72, 174, 76);
        public static readonly Color PrimaryHover = Color.FromArgb(64, 160, 68);
        public static readonly Color PrimaryDown = Color.FromArgb(53, 140, 57);
        public static readonly Color TextMain = Color.FromArgb(30, 34, 40);
        public static readonly Color TextSub = Color.FromArgb(136, 143, 152);
        public static readonly Color TextFaint = Color.FromArgb(160, 166, 174);
        public static readonly Color Ghost = Color.FromArgb(242, 244, 247);
        public static readonly Color GhostHover = Color.FromArgb(233, 236, 240);
        public static readonly Color GhostDown = Color.FromArgb(222, 226, 231);
        public static readonly Color GhostText = Color.FromArgb(70, 77, 85);
        public static readonly Color Divider = Color.FromArgb(238, 241, 245);
        public static readonly Color Track = Color.FromArgb(228, 232, 237);
        public static readonly Color Danger = Color.FromArgb(210, 66, 66);
    }

    static class Gfx
    {
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Bitmap Resize(Image src, int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }
            return bmp;
        }
    }

    // ============================================================ 圆角控件

    sealed class CardPanel : Panel
    {
        public int Radius = 12;
        public Color BorderColor = Theme.CardBorder;
        public bool DrawBorder = true;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Theme.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Gfx.RoundRect(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(BackColor))
                    g.FillPath(b, path);
                if (DrawBorder)
                {
                    using (Pen pen = new Pen(BorderColor))
                        g.DrawPath(pen, path);
                }
            }
        }
    }

    sealed class RoundButton : Button
    {
        public int Radius = 9;
        public Color Fill = Theme.Primary;
        public Color FillHover = Theme.PrimaryHover;
        public Color FillDown = Theme.PrimaryDown;
        public Color TextColor = Color.White;
        public bool BoldText = true;

        bool hovered;
        bool pressed;

        public RoundButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            Color fill = Enabled
                ? (pressed ? FillDown : (hovered ? FillHover : Fill))
                : Color.FromArgb(206, 212, 218);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Gfx.RoundRect(r, Radius))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, path);

            Color tc = Enabled ? TextColor : Color.White;
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, tc, flags);
        }
    }

    sealed class RoundTextBox : Panel
    {
        public TextBox Inner;
        public int Radius = 9;
        public Color BorderColor = Theme.CardBorder;
        public Color BorderFocus = Theme.Primary;
        int PadX { get { return (int)Math.Round(13 * DeviceDpi / 96.0); } }
        int PadY { get { return (int)Math.Round(8 * DeviceDpi / 96.0); } }

        public RoundTextBox(bool multiline)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Theme.Card;

            Inner = new TextBox();
            Inner.BorderStyle = BorderStyle.None;
            Inner.BackColor = Theme.Card;
            Inner.ForeColor = Theme.TextMain;
            Inner.Multiline = multiline;
            if (multiline)
            {
                Inner.ScrollBars = ScrollBars.Vertical;
                Inner.AcceptsReturn = true;
            }
            Inner.GotFocus += delegate { BorderColor = BorderFocus; Invalidate(); };
            Inner.LostFocus += delegate { BorderColor = Theme.CardBorder; Invalidate(); };
            Controls.Add(Inner);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Inner == null) return;
            if (Inner.Multiline)
                Inner.SetBounds(PadX, PadY, Math.Max(1, Width - PadX * 2), Math.Max(1, Height - PadY * 2));
            else
            {
                int h = Inner.PreferredHeight;
                Inner.SetBounds(PadX, Math.Max(0, (Height - h) / 2), Math.Max(1, Width - PadX * 2), h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Gfx.RoundRect(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(BackColor))
                    g.FillPath(b, path);
                using (Pen pen = new Pen(BorderColor))
                    g.DrawPath(pen, path);
            }
        }
    }

    sealed class RoundProgress : Control
    {
        int maxValue = 100;
        int curValue;

        public int MaxValue
        {
            get { return maxValue; }
            set { maxValue = Math.Max(1, value); Invalidate(); }
        }

        public int Value
        {
            get { return curValue; }
            set
            {
                int v = Math.Max(0, Math.Min(maxValue, value));
                if (v != curValue) { curValue = v; Invalidate(); }
            }
        }

        public RoundProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            Height = 6;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            int h = Math.Max(4, Height);
            Rectangle track = new Rectangle(0, 0, Width, h);
            using (GraphicsPath p = Gfx.RoundRect(track, h / 2))
            using (SolidBrush b = new SolidBrush(Theme.Track))
                g.FillPath(b, p);

            if (curValue <= 0) return;
            int w = (int)Math.Round((double)Width * curValue / maxValue);
            if (w < h) w = h;
            if (w > Width) w = Width;
            Rectangle fill = new Rectangle(0, 0, w, h);
            using (GraphicsPath p = Gfx.RoundRect(fill, h / 2))
            using (SolidBrush b = new SolidBrush(Theme.Primary))
                g.FillPath(b, p);
        }
    }

    // ============================================================ 主窗口

    sealed class MainForm : Form
    {
        TextBox txtLinks;
        TextBox txtDir;
        RoundButton btnBrowse;
        RoundButton btnStart;
        RoundButton btnOpen;
        RoundProgress bar;
        RichTextBox rtbLog;
        RoundTextBox boxLinks;
        RoundTextBox boxDir;
        string outDir;
        bool running;

float k = 1f;

        int S(int v) { return (int)Math.Round(v * k); }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hDC, int index);

        // .NET 的 Graphics.DpiX 在 DPI aware 进程里仍返回 96（缓存），必须直接问 GDI
        static float DetectScale()
        {
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                int dpi = GetDeviceCaps(dc, 88); // LOGPIXELSX
                ReleaseDC(IntPtr.Zero, dc);
                if (dpi <= 0) return 1f;
                return dpi / 96f;
            }
            catch (Exception) { return 1f; }
        }

        public MainForm(Icon appIcon)
        {
            // DPI 手动缩放：DPI aware 进程里 AutoScale 会算出反向因子，故关闭自动缩放
            k = DetectScale();
            if (k < 1f) k = 1f;
            if (k > 3f) k = 3f;

            // ---- 窗体
            Text = "酷安文章导出";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            Font = new Font("Microsoft YaHei", 9f, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;
            if (appIcon != null)
            {
                try { Icon = appIcon; }
                catch (Exception) { }
            }

            int winW = S(820), winH = S(700);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (winH > wa.Height - S(30)) winH = wa.Height - S(30);
            if (winW > wa.Width - S(30)) winW = wa.Width - S(30);
            ClientSize = new Size(winW, winH);
            MinimumSize = new Size(S(700), S(600));

            outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "酷安导出");

            // ---- 顶部标题栏
            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = S(86);
            header.BackColor = Color.White;
            header.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Divider))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };

            PictureBox logo = new PictureBox();
            logo.Size = new Size(S(46), S(46));
            logo.Location = new Point(S(22), S(20));
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = Color.White;
            logo.Image = MakeLogo(S(92));

            Label lblTitle = new Label();
            lblTitle.Text = "酷安文章导出";
            lblTitle.Font = new Font("Microsoft YaHei", 15f, FontStyle.Bold, GraphicsUnit.Point);
            lblTitle.ForeColor = Theme.TextMain;
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(S(84), S(18));

            Label lblSub = new Label();
            lblSub.Text = "粘贴分享链接，保存为本地 Markdown 与图片";
            lblSub.Font = new Font("Microsoft YaHei", 9f, FontStyle.Regular, GraphicsUnit.Point);
            lblSub.ForeColor = Theme.TextSub;
            lblSub.AutoSize = true;
            lblSub.Location = new Point(S(86), S(50));

            header.Controls.Add(logo);
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSub);

            // ---- 主体
            TableLayoutPanel main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.ColumnCount = 1;
            main.RowCount = 9;
            main.Padding = new Padding(S(22), S(18), S(22), S(18));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(158)));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(16)));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(88)));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(16)));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(44)));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(14)));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(8)));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, S(16)));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // ---- 卡片 1：链接
            CardPanel card1 = new CardPanel();
            card1.Dock = DockStyle.Fill;
            card1.Radius = S(12);
            card1.Padding = new Padding(S(16), S(12), S(16), S(14));
            card1.Margin = new Padding(0);

            TableLayoutPanel t1 = new TableLayoutPanel();
            t1.Dock = DockStyle.Fill;
            t1.ColumnCount = 1;
            t1.RowCount = 3;
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t1.RowStyles.Add(new RowStyle(SizeType.Absolute, S(24)));
            t1.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));
            t1.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            Label lb1 = NewLabel("分享链接", 9.5f, true, Theme.TextMain);
            lb1.Margin = new Padding(0, 0, 0, 0);

            Label lb1hint = NewLabel("每行一条，需带 ?s= 分享参数；可直接拖拽链接进来", 8f, false, Theme.TextFaint);
            lb1hint.Margin = new Padding(0, 0, 0, S(6));

            boxLinks = new RoundTextBox(true);
            boxLinks.Dock = DockStyle.Fill;
            boxLinks.Radius = S(10);
            boxLinks.Margin = new Padding(0);
            boxLinks.Inner.Font = new Font("Microsoft YaHei", 9f, FontStyle.Regular, GraphicsUnit.Point);
            boxLinks.Inner.ForeColor = Color.FromArgb(56, 62, 70);
            txtLinks = boxLinks.Inner;

            t1.Controls.Add(lb1, 0, 0);
            t1.Controls.Add(lb1hint, 0, 1);
            t1.Controls.Add(boxLinks, 0, 2);
            card1.Controls.Add(t1);

            // ---- 卡片 2：保存位置
            CardPanel card2 = new CardPanel();
            card2.Dock = DockStyle.Fill;
            card2.Radius = S(12);
            card2.Padding = new Padding(S(16), S(12), S(16), S(14));
            card2.Margin = new Padding(0);

            TableLayoutPanel t2 = new TableLayoutPanel();
            t2.Dock = DockStyle.Fill;
            t2.ColumnCount = 1;
            t2.RowCount = 2;
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t2.RowStyles.Add(new RowStyle(SizeType.Absolute, S(22)));
            t2.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));

            Label lb2 = NewLabel("保存位置", 9.5f, true, Theme.TextMain);
            lb2.Margin = new Padding(0, 0, 0, S(4));

            TableLayoutPanel t2row = new TableLayoutPanel();
            t2row.Dock = DockStyle.Fill;
            t2row.ColumnCount = 2;
            t2row.RowCount = 1;
            t2row.Margin = new Padding(0);
            t2row.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            t2row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t2row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(108)));

            boxDir = new RoundTextBox(false);
            boxDir.Dock = DockStyle.Fill;
            boxDir.Radius = S(10);
            boxDir.Margin = new Padding(0, 0, S(10), 0);
            boxDir.Inner.ReadOnly = true;
            boxDir.Inner.BackColor = Theme.Card;
            boxDir.Inner.Text = outDir;
            boxDir.Inner.ForeColor = Theme.TextSub;
            txtDir = boxDir.Inner;

            btnBrowse = NewGhostButton("选择目录");
            btnBrowse.Dock = DockStyle.Fill;
            btnBrowse.Margin = new Padding(0);
            btnBrowse.Radius = S(10);
            btnBrowse.Click += BtnBrowse_Click;

            t2row.Controls.Add(boxDir, 0, 0);
            t2row.Controls.Add(btnBrowse, 1, 0);

            t2.Controls.Add(lb2, 0, 0);
            t2.Controls.Add(t2row, 0, 1);
            card2.Controls.Add(t2);

            // ---- 按钮行
            TableLayoutPanel t3 = new TableLayoutPanel();
            t3.Dock = DockStyle.Fill;
            t3.ColumnCount = 2;
            t3.RowCount = 1;
            t3.Margin = new Padding(0);
            t3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(128)));

            btnStart = new RoundButton();
            btnStart.Text = "开始导出";
            btnStart.Font = new Font("Microsoft YaHei", 11f, FontStyle.Bold, GraphicsUnit.Point);
            btnStart.Dock = DockStyle.Fill;
            btnStart.Radius = S(10);
            btnStart.Margin = new Padding(0, 0, S(12), 0);
            btnStart.Click += BtnStart_Click;

            btnOpen = NewGhostButton("打开目录");
            btnOpen.Dock = DockStyle.Fill;
            btnOpen.Radius = S(10);
            btnOpen.Margin = new Padding(0);
            btnOpen.Enabled = false;
            btnOpen.Click += BtnOpen_Click;

            t3.Controls.Add(btnStart, 0, 0);
            t3.Controls.Add(btnOpen, 1, 0);

            // ---- 进度条
            bar = new RoundProgress();
            bar.Dock = DockStyle.Fill;
            bar.Margin = new Padding(0);

            // ---- 卡片 3：日志
            CardPanel card3 = new CardPanel();
            card3.Dock = DockStyle.Fill;
            card3.Radius = S(12);
            card3.Padding = new Padding(S(16), S(12), S(16), S(14));
            card3.Margin = new Padding(0);

            TableLayoutPanel t4 = new TableLayoutPanel();
            t4.Dock = DockStyle.Fill;
            t4.ColumnCount = 1;
            t4.RowCount = 2;
            t4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t4.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
            t4.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            Label lb3 = NewLabel("日志", 9.5f, true, Theme.TextMain);
            lb3.Margin = new Padding(0, 0, 0, S(4));

            rtbLog = new RichTextBox();
            rtbLog.Dock = DockStyle.Fill;
            rtbLog.BorderStyle = BorderStyle.None;
            rtbLog.BackColor = Theme.Card;
            rtbLog.ReadOnly = true;
            rtbLog.Font = new Font("Microsoft YaHei", 9f, FontStyle.Regular, GraphicsUnit.Point);
            rtbLog.ForeColor = Color.FromArgb(60, 64, 70);
            rtbLog.Margin = new Padding(0);
            rtbLog.HideSelection = false;
            rtbLog.DetectUrls = false;

            t4.Controls.Add(lb3, 0, 0);
            t4.Controls.Add(rtbLog, 0, 1);
            card3.Controls.Add(t4);

            main.Controls.Add(card1, 0, 0);
            main.Controls.Add(card2, 0, 2);
            main.Controls.Add(t3, 0, 4);
            main.Controls.Add(bar, 0, 6);
            main.Controls.Add(card3, 0, 8);

            Controls.Add(main);
            Controls.Add(header);

            // ---- 拖拽
            AllowDrop = true;
            DragEnter += delegate (object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.Text)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate (object s, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(DataFormats.Text)) return;
                string txt = e.Data.GetData(DataFormats.Text).ToString();
                if (txt.Trim().Length == 0) return;
                if (txtLinks.Text.Trim().Length > 0 && !txtLinks.Text.EndsWith("\n"))
                    txtLinks.AppendText("\r\n");
                txtLinks.AppendText(txt.Trim() + "\r\n");
            };

            LogDim("就绪。粘贴酷安分享链接后点击「开始导出」。");
        }

        // 与 app.ico 同构图的 Logo（不依赖 Icon.ToBitmap，避开 .NET 对 PNG 帧 ICO 的解析缺陷）
        static Bitmap MakeLogo(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                float pad = size * 0.0234f;
                RectangleF rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
                int radius = (int)Math.Round(size * 0.2266f);

                using (GraphicsPath path = Gfx.RoundRect(Rectangle.Round(rect), radius))
                using (LinearGradientBrush br = new LinearGradientBrush(rect,
                           Color.FromArgb(92, 196, 96), Color.FromArgb(52, 142, 56), 45f))
                    g.FillPath(br, path);

                using (Font f = new Font("Segoe UI", size * 0.5156f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("K", f, Brushes.White,
                        new RectangleF(0, -size * 0.024f, size, size), sf);
                }

                float cx = size * 0.766f;
                using (Pen pen = new Pen(Color.White, size * 0.0547f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, cx, size * 0.656f, cx, size * 0.805f);
                    g.DrawLine(pen, cx - size * 0.0703f, size * 0.742f, cx, size * 0.813f);
                    g.DrawLine(pen, cx + size * 0.0703f, size * 0.742f, cx, size * 0.813f);
                }
            }
            return bmp;
        }

        static Label NewLabel(string text, float size, bool bold, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font("Microsoft YaHei", size,
                bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            return l;
        }

        static RoundButton NewGhostButton(string text)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.Fill = Theme.Ghost;
            b.FillHover = Theme.GhostHover;
            b.FillDown = Theme.GhostDown;
            b.TextColor = Theme.GhostText;
            b.Font = new Font("Microsoft YaHei", 9f, FontStyle.Regular, GraphicsUnit.Point);
            b.Radius = 9;
            return b;
        }

        // ---------- 交互

        void BtnBrowse_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "选择导出目录";
            dlg.SelectedPath = outDir;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                outDir = dlg.SelectedPath;
                txtDir.Text = outDir;
            }
        }

        void BtnOpen_Click(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + outDir + "\"");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开目录"); }
        }

        void BtnStart_Click(object sender, EventArgs e)
        {
            if (running) return;

            string[] lines = txtLinks.Text.Split(new char[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            List<string> targets = new List<string>();
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Trim().Length > 0) targets.Add(lines[i].Trim());

            if (targets.Count == 0)
            {
                MessageBox.Show(this, "请先粘贴至少一条酷安分享链接。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            running = true;
            btnStart.Enabled = false;
            btnBrowse.Enabled = false;
            btnOpen.Enabled = false;
            rtbLog.Clear();
            bar.MaxValue = targets.Count;
            bar.Value = 0;

            string saveDir = outDir;
            List<string> jobs = targets;

            Log(string.Format("准备导出 {0} 篇 ...", jobs.Count), Theme.TextSub);

            Thread t = new Thread(delegate ()
            {
                int ok = 0;
                for (int i = 0; i < jobs.Count; i++)
                {
                    string one = jobs[i];
                    Log(string.Format("[{0}/{1}] {2}", i + 1, jobs.Count, one), Theme.TextSub);
                    try
                    {
                        Directory.CreateDirectory(saveDir);
                        FeedConverter conv = new FeedConverter(LogDim);
                        ConvertResult r = conv.Convert(one, saveDir);
                        if (r.Ok)
                        {
                            ok++;
                            Log("  ├ 完成：" + r.MdPath, Theme.Primary);
                            string imgLine = string.Format("  └ 图片 {0} 张", r.Images);
                            if (r.FailedImages > 0)
                                imgLine += string.Format("，失败 {0} 张（已保留原图链接）", r.FailedImages);
                            Log(imgLine, Theme.Primary);
                        }
                        else
                        {
                            Log("  └ 失败：" + r.Message, Theme.Danger);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("  └ 异常：" + ex.Message, Theme.Danger);
                    }
                    SetProgress(i + 1);
                    Log("", Theme.TextSub);
                }
                Finish(ok, jobs.Count, saveDir);
            });
            t.IsBackground = true;
            t.Start();
        }

        void Finish(int ok, int total, string saveDir)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, int, string>(Finish), ok, total, saveDir);
                return;
            }
            running = false;
            btnStart.Enabled = true;
            btnBrowse.Enabled = true;
            btnOpen.Enabled = ok > 0;

            if (ok == total)
                Log(string.Format("全部完成：{0}/{1}", ok, total), Theme.Primary);
            else
                Log(string.Format("结束：成功 {0}/{1}", ok, total), Theme.Danger);

            if (ok > 0) Log("输出目录：" + saveDir, Theme.TextSub);
        }

        void SetProgress(int value)
        {
            if (bar.InvokeRequired)
            {
                bar.BeginInvoke(new Action<int>(SetProgress), value);
                return;
            }
            bar.Value = value;
        }

        // ---------- 日志（按级别着色）

        void Log(string msg) { Log(msg, Color.FromArgb(60, 64, 70)); }
        void LogDim(string msg) { Log(msg, Theme.TextFaint); }

        void Log(string msg, Color color)
        {
            if (rtbLog.InvokeRequired)
            {
                rtbLog.BeginInvoke(new Action<string, Color>(Log), msg, color);
                return;
            }
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = color;
            rtbLog.AppendText(msg + "\r\n");
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.ScrollToCaret();
        }
    }

    // ============================================================ 入口

    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static Icon LoadAppIcon()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("Coolapk2MD.app.ico"))
                    if (s != null) return new Icon(s);
            }
            catch (Exception) { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }
            return null;
        }

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); }
            catch (Exception) { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Icon appIcon = LoadAppIcon();

            // 命令行模式（便于批处理 / 自动化）：Coolapk2MD.exe --cli <链接> [输出目录]
            if (args.Length >= 2 && (args[0] == "--cli" || String.Equals(args[0], "-c", StringComparison.OrdinalIgnoreCase)))
            {
                string outRoot = args.Length >= 3
                    ? args[2]
                    : Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "out");
                Directory.CreateDirectory(outRoot);

                StringBuilder logBuf = new StringBuilder();
                Action<string> sink = delegate (string s) { logBuf.AppendLine(s); };

                FeedConverter conv = new FeedConverter(sink);
                ConvertResult r = conv.Convert(args[1], outRoot);
                if (r.Ok) sink("OK " + r.MdPath + " images=" + r.Images);
                else sink("FAIL " + r.Message);

                string logPath = Path.Combine(outRoot, "coolapk2md.log.txt");
                try { File.WriteAllText(logPath, logBuf.ToString(), new UTF8Encoding(false)); }
                catch (Exception) { }
                Environment.Exit(r.Ok ? 0 : 1);
                return;
            }

            Application.Run(new MainForm(appIcon));
        }
    }
}