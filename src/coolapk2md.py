#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
coolapk2md —— 把酷安图文文章 / 动态 转成本地 Markdown + 图片。

零第三方依赖（只用 Python 标准库），支持 Python 3.8+。

核心原理
--------
酷安网页版对不含分享令牌的链接只返回「请用酷安手机APP扫码查看详情」壳页。
只有带 `?s=` 分享令牌的链接才会返回完整正文 HTML（令牌绑定 feed id，不绑定 UA/IP）。
因此：从酷安 App 分享出来的链接（一定带 s）可以完整抓取。

正文 HTML 结构（实测 2026-11）
    可选标题   div.message-title > p
    作者/时间  div.common-userinfo-group > div.username-item > p*2
    封面       div.message-cover > img
    正文       div.feed-article-message（图文长文） 或 div.feed-message（普通动态）
    图片       div.message-image-item > img.message-image
    链接       a.feed-link-url
    换行       <br />
"""
from __future__ import annotations

import argparse
import html
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from html.parser import HTMLParser

VERSION = "1.0.0"

DEFAULT_UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36")

SHELL_MARKERS = ("请用酷安手机APP扫码", "酷安手机APP扫码查看详情")

VOID_TAGS = {"area", "base", "br", "col", "embed", "hr", "img", "input",
             "link", "meta", "param", "source", "track", "wbr"}

INLINE_TAGS = {"a", "span", "b", "strong", "i", "em", "u", "s", "del", "code",
               "font", "sub", "sup", "small", "mark", "abbr", "time", "cite"}

BLOCK_TAGS = {"p", "div", "section", "article", "blockquote", "figure",
              "table", "hr", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6"}


# ---------------------------------------------------------------- HTTP

def http_get(url: str, timeout: int = 30, retries: int = 3) -> bytes:
    last = None
    for attempt in range(1, retries + 1):
        try:
            req = urllib.request.Request(url, headers={
                "User-Agent": DEFAULT_UA,
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                "Accept-Language": "zh-CN,zh;q=0.9",
                "Referer": "https://www.coolapk.com/",
            })
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except Exception as e:  # noqa: BLE001
            last = e
            if attempt < retries:
                time.sleep(1.2 * attempt)
    raise RuntimeError(f"下载失败 {url} -> {type(last).__name__}: {last}")


def http_download(url: str, dest: str, timeout: int = 60, retries: int = 3) -> bool:
    """下载单个文件，成功返回 True。"""
    last = None
    for attempt in range(1, retries + 1):
        try:
            req = urllib.request.Request(url, headers={
                "User-Agent": DEFAULT_UA,
                "Referer": "https://www.coolapk.com/",
                "Accept": "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
            })
            with urllib.request.urlopen(req, timeout=timeout) as r:
                data = r.read()
                if not data:
                    raise ValueError("空响应")
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                with open(dest, "wb") as f:
                    f.write(data)
                return True
        except Exception as e:  # noqa: BLE001
            last = e
            if attempt < retries:
                time.sleep(1.2 * attempt)
    print(f"    [warn] 图片下载失败 {url} ({type(last).__name__})", file=sys.stderr)
    return False


# ---------------------------------------------------------------- 迷你 DOM

class Node:
    __slots__ = ("tag", "attrs", "children", "parent")

    def __init__(self, tag: str, attrs: dict | None = None, parent=None):
        self.tag = tag
        self.attrs = attrs or {}
        self.children: list = []
        self.parent = parent

    def cls(self) -> str:
        return self.attrs.get("class", "") or ""

    def has_cls(self, name: str) -> bool:
        return name in self.cls().split()

    def __repr__(self) -> str:  # pragma: no cover
        return f"<{self.tag} class={self.cls()!r} children={len(self.children)}>"


class TreeBuilder(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=False)
        self.root = Node("#document")
        self.stack = [self.root]
        self.skip = 0

    def handle_starttag(self, tag, attrs):
        tag = tag.lower()
        if tag in ("script", "style"):
            self.skip += 1
            return
        if self.skip:
            return
        node = Node(tag, dict(attrs), parent=self.stack[-1])
        self.stack[-1].children.append(node)
        if tag not in VOID_TAGS:
            self.stack.append(node)

    def handle_startendtag(self, tag, attrs):
        tag = tag.lower()
        if self.skip:
            return
        node = Node(tag, dict(attrs), parent=self.stack[-1])
        self.stack[-1].children.append(node)

    def handle_endtag(self, tag):
        tag = tag.lower()
        if tag in ("script", "style"):
            self.skip = max(0, self.skip - 1)
            return
        if self.skip:
            return
        for i in range(len(self.stack) - 1, 0, -1):
            if self.stack[i].tag == tag:
                del self.stack[i:]
                return

    def handle_data(self, data):
        if self.skip:
            return
        self.stack[-1].children.append(data)

    def handle_comment(self, data):
        return

    # convert_charrefs=False 时，字符引用走这两个回调而不是 handle_data，
    # 不实现它们会静默吞掉 &gt; &amp; &nbsp; 等实体（正文里的 -> 会变成 -）。
    def handle_entityref(self, name):
        if self.skip:
            return
        self.stack[-1].children.append(html.unescape(f"&{name};"))

    def handle_charref(self, name):
        if self.skip:
            return
        self.stack[-1].children.append(html.unescape(f"&#{name};"))


def parse_html(text: str) -> Node:
    b = TreeBuilder()
    b.feed(text)
    b.close()
    return b.root


def walk(node):
    for ch in node.children:
        if isinstance(ch, Node):
            yield ch
            yield from walk(ch)


def find_all(node, pred) -> list:
    return [n for n in walk(node) if pred(n)]


def find_first(node, pred):
    for n in walk(node):
        if pred(n):
            return n
    return None


def text_of(node) -> str:
    """取节点纯文本（含子节点），空白压缩。"""
    if isinstance(node, str):
        return node
    parts = []
    for ch in node.children:
        parts.append(text_of(ch))
    return "".join(parts)


def norm_ws(s: str) -> str:
    return re.sub(r"[ \t\r\n\u00a0]+", " ", html.unescape(s)).strip()


# ---------------------------------------------------------------- 输入解析

def parse_target(raw: str) -> tuple[str | None, str | None]:
    """从分享链接/纯 id 中取出 (feed_id, s_token)。"""
    raw = raw.strip()
    fid = None
    m = re.search(r"coolapk\.com/feed/(\d+)", raw) or re.search(r"/feed/(\d+)", raw)
    if m:
        fid = m.group(1)
    elif re.fullmatch(r"\d{4,}", raw):
        fid = raw

    tok = None
    mt = re.search(r"[?&]s=([A-Za-z0-9%_\-]+)", raw)
    if mt:
        tok = urllib.parse.unquote(mt.group(1))
    return fid, tok


# ---------------------------------------------------------------- 转换上下文

class Ctx:
    def __init__(self, outdir: str, imgdir_name: str = "images"):
        self.outdir = outdir
        self.imgdir_name = imgdir_name
        self.img_no = 0
        self.saved: list[tuple[str, str]] = []   # (url, local_path)
        self.failed: list[str] = []
        self._used = set()

    def next_index(self) -> int:
        self.img_no += 1
        return self.img_no

    def download_image(self, url: str, idx: int) -> str | None:
        """返回相对 outdir 的本地路径，如 images/01.jpg；失败返回 None。"""
        base = f"{idx:02d}"
        ext = guess_ext(url)
        for n in range(0, 100):
            name = f"{base}{'' if n == 0 else '_' + str(n)}{ext}"
            rel = os.path.join(self.imgdir_name, name)
            abs_ = os.path.join(self.outdir, rel)
            if rel not in self._used and not os.path.exists(abs_):
                self._used.add(rel)
                break
        if http_download(url, abs_):
            real = name
            # 若 content-type 与猜测扩展名不符，以实际字节为准
            real = fix_ext_by_magic(abs_, real)
            if real != name:
                os.replace(abs_, os.path.join(self.outdir, self.imgdir_name, real))
                rel = os.path.join(self.imgdir_name, real)
            self.saved.append((url, rel))
            return rel.replace(os.sep, "/")
        self.failed.append(url)
        return None


def guess_ext(url: str) -> str:
    path = urllib.parse.urlparse(url).path.lower()
    path = re.sub(r"\.m\.(jpe?g|png|webp|gif)$", r".\1", path)
    m = re.search(r"\.(jpe?g|png|webp|gif|bmp)$", path)
    if m:
        e = m.group(1)
        return ".jpg" if e == "jpeg" else "." + e
    return ".jpg"


def fix_ext_by_magic(path: str, name: str) -> str:
    try:
        with open(path, "rb") as f:
            head = f.read(16)
    except OSError:
        return name
    ext = None
    if head.startswith(b"\xff\xd8\xff"):
        ext = ".jpg"
    elif head.startswith(b"\x89PNG"):
        ext = ".png"
    elif head[:4] == b"GIF8":
        ext = ".gif"
    elif head[:4] == b"RIFF" and head[8:12] == b"WEBP":
        ext = ".webp"
    if ext and not name.endswith(ext):
        return os.path.splitext(name)[0] + ext
    return name


def norm_url(u: str) -> str:
    u = (u or "").strip()
    if not u:
        return ""
    if u.startswith("//"):
        return "https:" + u
    if u.startswith("/"):
        return "https://www.coolapk.com" + u
    return u


# ---------------------------------------------------------------- Markdown 渲染

# 源站部分 <a> 的 title 属性内嵌了未转义的 <span>，会把 `" target="_blank"
# rel="nofollow">` 漏进正文文本，这里统一清掉。
GARBAGE_ATTR = re.compile(
    r'"\s*(?:target|rel|class|title|style|href|align|width|height|id|name)\s*=\s*"[^"]*"?\s*>?'
)
GARBAGE_ATTR2 = re.compile(
    r'\s*(?:target|rel|class|title|style|href|align|width|height|id|name)\s*=\s*"[^"]*"?\s*>?'
)


def clean_label(s: str) -> str:
    s = GARBAGE_ATTR.sub("", s)
    s = GARBAGE_ATTR2.sub("", s)
    s = re.sub(r"\s+", " ", s).strip()
    # 畸形标签常导致同一段文字出现两遍
    n = len(s)
    if n >= 12 and n % 2 == 0 and s[: n // 2] == s[n // 2:]:
        s = s[: n // 2].strip()
    return s


def md_url(u: str) -> str:
    """把含中文等非 ASCII 字符的 URL 编码成 Markdown 可用的形式。"""
    if not u or all(ord(c) < 128 for c in u):
        return u
    try:
        p = urllib.parse.urlsplit(u)
        return urllib.parse.urlunsplit((
            p.scheme, p.netloc,
            urllib.parse.quote(p.path, safe="/%"),
            urllib.parse.quote(p.query, safe="=&%?+/"),
            p.fragment,
        ))
    except Exception:  # noqa: BLE001
        return u


def md_node(ch, ctx: Ctx) -> str:
    """渲染单个节点本身（含其子节点）。"""
    if isinstance(ch, str):
        # 源码里的换行/缩进按 HTML 规则折叠成单个空格，真正的换行只来自 <br>
        t = html.unescape(ch).replace("\u00a0", " ")
        return re.sub(r"[ \t\r\n]+", " ", t)

    tag = ch.tag
    if tag == "br":
        return "\n"
    if tag == "img":
        return md_image(ch, ctx)

    if tag == "a":
        href = norm_url(ch.attrs.get("href", ""))
        label = clean_label(norm_ws(text_of(ch))) or ch.attrs.get("title", "") or href
        if href and href.lower().startswith(("javascript:", "#")):
            return label
        if href:
            return f"[{label}]({md_url(href)})"
        return label

    inner = md_inline(ch, ctx)
    if tag in BLOCK_TAGS:
        return "\n" + inner + "\n"
    return inner


def md_inline(node, ctx: Ctx) -> str:
    return "".join(md_node(ch, ctx) for ch in node.children)


# 行首若像 Markdown 块语法（标题/引用/列表/表格），加反斜杠转义，避免被渲染器吃掉
LINE_START_ESCAPE = re.compile(r"^([>#|]|[-*+]\s|\d+[.)]\s)")


def esc_line_start(ln: str) -> str:
    if LINE_START_ESCAPE.match(ln):
        return "\\" + ln
    return ln


def tidy_inline(s: str) -> str:
    """行内文本规范化：压空白、软换行变 Markdown 硬换行、连续空行折叠成一个空行。"""
    s = s.replace("\r", "")
    s = re.sub(r"[ \t]+", " ", s)
    raw = [ln.strip() for ln in s.split("\n")]

    lines: list[str] = []
    for ln in raw:
        if not ln:
            if lines and lines[-1] != "":
                lines.append("")
            continue
        lines.append(ln)
    while lines and lines[-1] == "":
        lines.pop()
    if not lines:
        return ""
    body = "\n".join((esc_line_start(ln) + "  ") if ln else "" for ln in lines)
    return body.rstrip()


def md_image(img: Node, ctx: Ctx) -> str:
    # 正文里的酷安表情包（feed-emoji）不是文章配图，下载下来只会污染图片目录
    if "feed-emoji" in img.cls() or "static.coolapk.com/emoticons" in (img.attrs.get("src") or ""):
        label = (img.attrs.get("alt") or img.attrs.get("title") or "").strip()
        return label

    src = img.attrs.get("src") or img.attrs.get("data-src") or ""
    src = norm_url(src)
    if not src:
        return ""
    idx = ctx.next_index()
    rel = ctx.download_image(src, idx)
    alt = f"图{idx}"
    if rel:
        return f"\n\n![{alt}]({rel})\n\n"
    return f"\n\n![{alt}]({src})\n\n"


def render_blocks(node: Node, ctx: Ctx) -> list[str]:
    """把正文容器渲染成 Markdown 块列表。"""
    blocks: list[str] = []
    buf: list[str] = []

    def flush():
        if buf:
            s = tidy_inline("".join(buf))
            if s.strip():
                blocks.append(s)
            buf.clear()

    for ch in node.children:
        if isinstance(ch, str):
            buf.append(re.sub(r"[ \t\r\n]+", " ",
                              html.unescape(ch).replace("\u00a0", " ")))
            continue

        cls = ch.cls()
        if "message-image-item" in cls or ch.tag == "figure":
            flush()
            for img in find_all(ch, lambda n: n.tag == "img"):
                s = md_image(img, ctx)
                if s.strip():
                    blocks.append(s.strip())
            continue

        if ch.tag in ("li",):
            flush()
            inner = tidy_inline(md_inline(ch, ctx))
            if inner.strip():
                blocks.append("- " + inner.replace("  \n", "  \n  "))
            continue

        if ch.tag in ("h1", "h2", "h3", "h4", "h5", "h6"):
            flush()
            level = int(ch.tag[1])
            t = tidy_inline(md_inline(ch, ctx)).replace("  \n", " ")
            if t.strip():
                blocks.append("#" * min(level + 1, 6) + " " + t.strip())
            continue

        if ch.tag == "hr":
            flush()
            blocks.append("---")
            continue

        if ch.tag == "img":
            buf.append(md_image(ch, ctx))
            continue

        if ch.tag in BLOCK_TAGS:
            flush()
            sub = render_blocks(ch, ctx)
            blocks.extend(sub)
            continue

        # 其它内联元素（a / span / code ...）
        buf.append(md_node(ch, ctx))

    flush()
    return blocks


# ---------------------------------------------------------------- 抓取 + 解析

def fetch_feed(feed_id: str, token: str | None) -> tuple[str, str]:
    """返回 (html, 实际 url)。"""
    if not token:
        raise ValueError(
            "缺少分享令牌 s。酷安网页版对无 s 的链接只返回壳页。\n"
            "   解决：在酷安 App 里打开该文章 -> 右上角分享 -> 复制链接，把完整链接（含 ?s=...）交给本工具。"
        )
    url = f"https://www.coolapk.com/feed/{feed_id}?s={urllib.parse.quote(token, safe='')}"
    raw = http_get(url)
    text = raw.decode("utf-8", "replace")
    if any(m in text for m in SHELL_MARKERS):
        raise ValueError(
            f"服务端返回了壳页（令牌可能已失效或与 feed/{feed_id} 不匹配）。\n"
            "   解决：重新在 App 内分享该文章复制链接。"
        )
    return text, url


def extract_meta(root: Node) -> dict:
    detail = find_first(root, lambda n: n.attrs.get("id") == "feed-detail") or root

    title = ""
    t_node = find_first(detail, lambda n: n.has_cls("message-title"))
    if t_node:
        title = norm_ws(text_of(t_node))

    author = ""
    date = ""
    device = ""
    u_node = find_first(detail, lambda n: n.has_cls("username-item"))
    if u_node:
        ps = [c for c in u_node.children if isinstance(c, Node) and c.tag == "p"]
        if ps:
            author = norm_ws(text_of(ps[0]))
        if len(ps) > 1:
            sp = find_first(ps[1], lambda n: n.tag == "span")
            full = norm_ws(text_of(ps[1]))
            if sp:
                device = norm_ws(text_of(sp))
                full = full.replace(device, "").strip()
            date = full

    cover = ""
    c_node = find_first(detail, lambda n: n.has_cls("message-cover"))
    if c_node:
        img = find_first(c_node, lambda n: n.tag == "img")
        if img:
            cover = norm_url(img.attrs.get("src", ""))

    return {"title": title, "author": author, "date": date,
            "device": device, "cover": cover, "detail": detail}


def first_line_of(node: Node) -> str:
    """取正文第一段的首行（遇到 <br> 截止），用于无标题动态。"""
    for ch in node.children:
        if not isinstance(ch, Node):
            continue
        if ch.tag in ("div", "section", "article"):
            t = first_line_of(ch)
            if t:
                return t
            continue
        if ch.tag != "p":
            continue
        parts: list[str] = []
        for c in ch.children:
            if isinstance(c, str):
                parts.append(html.unescape(c))
            elif c.tag == "br":
                break
            elif c.tag in INLINE_TAGS:
                parts.append(text_of(c))
            else:
                break
        t = norm_ws("".join(parts))
        if t:
            return t
    return ""


def extract_body(detail: Node) -> Node | None:
    """取正文。

    图文长文的配图在 feed-article-message 内部；
    普通动态的文字在 feed-message，配图却在隔壁的 message-image-group，
    所以这里用一个虚拟容器把「文字 + 紧随其后的图片组」按顺序串起来。
    """
    text_node = None
    for cls in ("feed-article-message", "feed-message"):
        text_node = find_first(detail, lambda x, c=cls: x.has_cls(c))
        if text_node is not None:
            break
    if text_node is None:
        return None

    virtual = Node("div")
    virtual.children.append(text_node)

    parent = text_node.parent
    if parent is not None:
        started = False
        for ch in parent.children:
            if ch is text_node:
                started = True
                continue
            if not started or not isinstance(ch, Node):
                continue
            cls = ch.cls()
            # 正文结束的标志节点
            if any(c in cls for c in ("feed-status", "feed-hot-reply", "pswp",
                                      "common-userinfo-group", "message-cover")):
                break
            if "message-image-group" in cls or "message-image-item" in cls:
                virtual.children.append(ch)
    return virtual


# ---------------------------------------------------------------- 落盘

ILLEGAL = re.compile(r'[\\/:*?"<>|\x00-\x1f]')


def safe_name(s: str, limit: int = 60) -> str:
    s = ILLEGAL.sub("_", s or "").strip().strip(".")
    s = re.sub(r"\s+", " ", s)
    return s[:limit].strip() or ""


def convert(raw: str, outroot: str, quiet: bool = False) -> str | None:
    feed_id, token = parse_target(raw)
    if not feed_id:
        print(f"[error] 无法识别 feed id：{raw}", file=sys.stderr)
        return None

    if not quiet:
        print(f"[1/4] 抓取 feed/{feed_id} ...")
    try:
        page, url = fetch_feed(feed_id, token)
    except Exception as e:  # noqa: BLE001
        print(f"[error] {e}", file=sys.stderr)
        return None

    if not quiet:
        print(f"[2/4] 解析 HTML ({len(page)} 字符) ...")
    root = parse_html(page)
    meta = extract_meta(root)
    body = extract_body(meta["detail"])
    if body is None:
        print("[error] 未找到正文容器（页面结构可能已改版）。", file=sys.stderr)
        return None

    title = meta["title"] or ""
    if not title:
        # 普通动态没有标题，取正文首行当标题
        head = first_line_of(body)[:30].strip()
        title = head or f"酷安动态_{feed_id}"
    dirname = safe_name(title) or f"feed_{feed_id}"
    outdir = os.path.join(outroot, dirname)
    if os.path.exists(outdir):
        alt = f"{dirname}_{feed_id}"
        outdir = os.path.join(outroot, alt)
    ctx = Ctx(outdir)

    # 封面先下载，编号排在正文图之前，顺序与阅读顺序一致
    cover_local = None
    if meta["cover"]:
        cover_local = ctx.download_image(meta["cover"], ctx.next_index())

    if not quiet:
        print(f"[3/4] 转换正文 -> {outdir}")
    blocks = render_blocks(body, ctx)

    # ---- 组装 Markdown
    lines: list[str] = []
    lines.append("---")
    lines.append(f"title: {json_quote(title)}")
    if meta["author"]:
        lines.append(f"author: {json_quote(meta['author'])}")
    if meta["date"]:
        lines.append(f"date: {json_quote(meta['date'])}")
    if meta["device"]:
        lines.append(f"device: {json_quote(meta['device'])}")
    lines.append(f"feed_id: {json_quote(feed_id)}")
    lines.append(f"source: {json_quote(url)}")
    lines.append("---")
    lines.append("")
    lines.append(f"# {title}")
    lines.append("")

    meta_line = []
    if meta["author"]:
        meta_line.append(f"作者：{meta['author']}")
    if meta["date"]:
        meta_line.append(f"时间：{meta['date']}")
    if meta["device"]:
        meta_line.append(f"机型：{meta['device']}")
    if meta_line:
        lines.append("> " + " ｜ ".join(meta_line))
        lines.append("")
    lines.append(f"> 原文：{url}")
    lines.append("")

    if cover_local:
        lines.append(f"![封面]({cover_local})")
        lines.append("")

    lines.append("\n\n".join(b for b in blocks if b.strip()))
    lines.append("")

    os.makedirs(outdir, exist_ok=True)
    md_path = os.path.join(outdir, "index.md")
    with open(md_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))

    if not quiet:
        print(f"[4/4] 完成")
        print(f"      Markdown : {md_path}")
        print(f"      图片     : {len(ctx.saved)} 张 -> {os.path.join(outdir, ctx.imgdir_name)}")
        if ctx.failed:
            print(f"      失败     : {len(ctx.failed)} 张（已在 md 中保留原链接）")
    return md_path


def json_quote(s: str) -> str:
    s = str(s).replace("\\", "\\\\").replace('"', '\\"')
    return f'"{s}"'


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(
        description="酷安图文/动态 -> 本地 Markdown + 图片（零依赖）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""示例：
  python coolapk2md.py "https://www.coolapk.com/feed/72078845?s=NTQy..."
  python coolapk2md.py "https://.../feed/72078845?s=xxx" "https://.../feed/67928960?s=yyy"
  python coolapk2md.py -o D:\\Notes\\酷安 "https://.../feed/72078845?s=xxx"
  python coolapk2md.py -f urls.txt

注意：链接必须带 ?s= 分享令牌（App 内分享出来的链接天然带），
      裸链接或裸 id 会触发壳页，工具会提示重新分享。""")
    ap.add_argument("targets", nargs="*", help="分享链接或 feed id")
    ap.add_argument("-o", "--out", default="out", help="输出根目录（默认 ./out）")
    ap.add_argument("-f", "--file", help="从文本文件批量读取链接，一行一个")
    ap.add_argument("-q", "--quiet", action="store_true", help="静默模式")
    ap.add_argument("-v", "--version", action="version", version=f"coolapk2md {VERSION}")
    args = ap.parse_args(argv)

    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:  # noqa: BLE001
        pass

    targets = list(args.targets)
    if args.file:
        with open(args.file, "r", encoding="utf-8") as f:
            targets += [ln.strip() for ln in f if ln.strip() and not ln.strip().startswith("#")]
    if not targets:
        ap.print_help()
        return 2

    os.makedirs(args.out, exist_ok=True)
    ok = 0
    for t in targets:
        if not args.quiet:
            print(f"=== {t}")
        if convert(t, args.out, args.quiet):
            ok += 1
        if not args.quiet:
            print()
    print(f"完成 {ok}/{len(targets)}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())