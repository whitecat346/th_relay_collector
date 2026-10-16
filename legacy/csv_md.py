#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import sys
import csv
import io
from tokenize import Double

# ---------- 强制 stdout 使用 UTF-8 ----------
if hasattr(sys.stdout, 'buffer'):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

# ---------- 宣传部分默认配置 ----------
DEFAULT_PROMOTION = {
    "主催": ("", ""),
    "一宣": "",
    "二宣": "",
    "终宣": "",
}

def detect_encoding(file_path):
    """尝试用常见编码读取文件，返回第一个成功的编码及文件内容（字符串）。"""
    encodings = ['gb2312', 'gbk', 'utf-8', 'utf-8-sig']
    for enc in encodings:
        try:
            with open(file_path, 'rb') as f:
                raw = f.read()
            text = raw.decode(enc)
            # 简单校验：能正常拆分行
            _ = text.splitlines()
            return enc, text
        except UnicodeDecodeError:
            continue
    raise UnicodeDecodeError(f"无法使用以下编码解析文件：{encodings}")

def generate_markdown(csv_file):
    encoding, content = detect_encoding(csv_file)
    f = io.StringIO(content)
    reader = csv.reader(f)

    # 跳过标题行
    try:
        header = next(reader)
    except StopIteration:
        header = []

    # 读取并过滤：确保有至少6列，且棒数不为空
    rows = []
    for row in reader:
        if len(row) < 6:
            continue
        num = row[0].strip()
        if num == '':
            continue   # 跳过棒数为空的行（如末尾空行）
        rows.append(row)

    normal = []
    ex = []

    for row in rows:
        num = row[0].strip()
        time = row[1].strip()
        author = row[2].strip()
        author_link = row[3].strip()
        typ = row[4].strip()
        work_link = row[5].strip()

        # 识别 EX 棒：棒数含 'ex' 或时间为 '9:19'
        if 'ex' in num.lower():
            ex.append((num, time, author, author_link, typ, work_link))
        else:
            normal.append((num, time, author, author_link, typ, work_link))

    # 按数字排序普通棒（此时 num 必定是纯数字）
    normal.sort(key=lambda x: float(x[0]))
    ex.sort(key=lambda x: x[1])   # 按时间排序

    out = []

    # ---------- 宣传 ----------
    out.append("## 宣传")
    out.append("")
    main_name, main_link = DEFAULT_PROMOTION["主催"]
    out.append(f"主催 [{main_name}]({main_link})")
    out.append("")
    out.append(f"一宣：[查看]({DEFAULT_PROMOTION['一宣']})")
    out.append("")
    out.append(f"二宣：[查看]({DEFAULT_PROMOTION['二宣']})")
    out.append("")
    out.append(f"终宣：[查看]({DEFAULT_PROMOTION['终宣']})")
    out.append("")

    # ---------- 整点接力 ----------
    out.append("## 整点接力")
    out.append("")

    for num, time, author, author_link, typ, work_link in normal:
        out.append(f"### 第{num}棒 {time}")
        out.append("")
        out.append(f"创作者 [@{author}]({author_link})")
        out.append("")
        out.append(f"{typ} [查看]({work_link})")
        out.append("")
        out.append("---")
        out.append("")

    # ---------- EX 棒 ----------
    if ex:
        out.append("## ex棒")
        out.append("")
        for num, time, author, author_link, typ, work_link in ex:
            if num.lower() == 'ex':
                title = f"### ex棒 {time}"
            else:
                title = f"### {num} {time}"
            out.append(title)
            out.append("")
            out.append(f"创作者 [@{author}]({author_link})")
            out.append("")
            out.append(f"{typ} [查看]({work_link})")
            out.append("")
            out.append("---")
            out.append("")

    return "\n".join(out)

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("用法: python script.py 输入.csv 输出.md")
        sys.exit(1)

    csv_path = sys.argv[1]
    md_path = sys.argv[2]
    try:
        markdown = generate_markdown(csv_path)
        f = open(md_path, "w")
        f.write(markdown)
    except Exception as e:
        print(f"错误: {e}", file=sys.stderr)
        sys.exit(1)