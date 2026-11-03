---
title: "用 Markdown 整理手机里的碎片笔记"
author: "示例用户"
date: "2025-08-14 21:33"
device: "OnePlus 13"
source: "https://www.coolapk.com/feed/00000000?s=EXAMPLE-TOKEN"
---

# 用 Markdown 整理手机里的碎片笔记

> 示例用户｜2025-08-14 21:33｜OnePlus 13

> 原文：https://www.coolapk.com/feed/00000000?s=EXAMPLE-TOKEN

这是 Coolapk2MD 的**输出格式示例**，内容为虚构，仅用于展示结构。

## 一、为什么选 Markdown

纯文本、可版本控制、不绑定任何软件。十年后还能打开，这是 \# 最重要的理由。

![配图 1](images/02.jpg)

段落内的行内代码写作 \`console.log()\` 这样，链接写作 [酷安](https://www.coolapk.com) 这样。

## 二、文件怎么放

一条笔记一个文件，文件名用日期加关键词：

\`\`\`text
2025-08-14-酷安导出.md
2025-08-15-读书笔记.md
\`\`\`

![配图 2](images/03.jpg)

> 这一行是引用。原文里的引用块会被保留。

## 三、同步方案

| 方案 | 成本 | 适合 |
| --- | --- | --- |
| 坚果云 | 免费额度够用 | 个人 |
| Git 仓库 | 需要一点学习 | 折腾党 |
| 局域网 Syncthing | 零成本 | 多设备 |

以上就是一个完整的输出示例：YAML front matter、标题、作者行、原文链接、正文、配图、引用、表格、代码块。