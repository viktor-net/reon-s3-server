#!/usr/bin/env python3
"""Демо-данные для Reon S3 Server: план, загрузка в S3 и SQL для Postgres.

Скрипт выполняется внутри контейнера Swift (в образе есть boto3) и печатает в
stdout либо план в JSON, либо SQL для psql. Оркестратор — ``seed.sh``.

Порядок записи повторяет README сервиса, раздел «Порядок записи»:

1. ``manifest``    — план: идентификаторы, ключи, размер и SHA-256;
2. ``sql-pending`` — строки ``files`` в статусе ``Pending``;
3. ``upload``      — объекты в S3 с metadata ``reon-file-id`` и ``reon-sha256``;
4. ``sql-ready``   — перевод строк в ``Ready`` с ETag и версией объекта из S3.

Дополнительные режимы:

* ``sql-statuses`` — строки в остальных статусах жизненного цикла;
* ``sql-admin``    — ``bucket_settings`` и ``audit_events``;
* ``sql-clean``    — удаление только сид-строк;
* ``clean-s3``     — удаление только сид-объектов и демо-бакетов.

Идентификаторы детерминированные (uuid5), поэтому повторный запуск обновляет
те же строки и объекты, а не создаёт дубликаты.
"""

from __future__ import annotations

import gzip
import hashlib
import io
import json
import os
import random
import struct
import sys
import uuid
import zipfile
import zlib
from datetime import datetime, timedelta, timezone

sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

NAMESPACE = uuid.UUID("5d0f1d2e-7c1a-4b1e-9f5a-6f0f3c2b1a90")
MAIN_BUCKET = os.environ.get("SEED_BUCKET", "reon-files")
KEY_PREFIX = os.environ.get("SEED_KEY_PREFIX", "files/")
OWNER = os.environ.get("SEED_OWNER", "system")
S3_ENDPOINT = os.environ.get("SEED_S3_ENDPOINT", "http://127.0.0.1:8080")
S3_ACCESS_KEY = os.environ.get("SEED_S3_ACCESS_KEY", "test:tester")
S3_SECRET_KEY = os.environ.get("SEED_S3_SECRET_KEY", "testing")
S3_REGION = os.environ.get("SEED_S3_REGION", "us-east-1")

NOW = datetime.now(timezone.utc).replace(microsecond=0)


# ---------------------------------------------------------------------------
# Содержимое демо-файлов
# ---------------------------------------------------------------------------

REGISTRY_OBJECTS = [
    {
        "uno": "77-0001234",
        "cadastral": "77:01:0001001:1234",
        "type": "Здание",
        "address": "г. Москва, ул. Тверская, д. 1, стр. 2",
        "area": 4820.5,
        "value": 812_450_000,
        "status": "Актуальный",
        "right": "собственность города Москвы",
        "purpose": "административное",
    },
    {
        "uno": "77-0001235",
        "cadastral": "77:01:0001002:5678",
        "type": "Земельный участок",
        "address": "г. Москва, ул. Примерная, вл. 7",
        "area": 1250.0,
        "value": 96_300_000,
        "status": "Актуальный",
        "right": "аренда",
        "purpose": "для размещения административного здания",
    },
    {
        "uno": "77-0001236",
        "cadastral": "77:02:0003003:9012",
        "type": "Помещение",
        "address": "г. Москва, Ленинградский пр-т, д. 10, пом. 3",
        "area": 128.4,
        "value": 24_700_000,
        "status": "Актуальный",
        "right": "собственность",
        "purpose": "нежилое",
    },
    {
        "uno": "77-0001237",
        "cadastral": "77:03:0004004:3456",
        "type": "Сооружение",
        "address": "г. Москва, наб. Речная, д. 5",
        "area": 320.0,
        "value": 51_200_000,
        "status": "На проверке",
        "right": "собственность города Москвы",
        "purpose": "транспортное сооружение",
    },
    {
        "uno": "77-0001238",
        "cadastral": "77:04:0005005:7890",
        "type": "Земельный участок",
        "address": "г. Москва, ул. Полевая, вл. 12",
        "area": 840.0,
        "value": 64_800_000,
        "status": "Архивный",
        "right": "собственность города Москвы",
        "purpose": "для индивидуального жилищного строительства",
    },
    {
        "uno": "77-0001239",
        "cadastral": "77:01:0001001:4321",
        "type": "Объект незавершённого строительства",
        "address": "г. Москва, ул. Тверская, вл. 3",
        "area": 2100.0,
        "value": 145_000_000,
        "status": "Актуальный",
        "right": "собственность города Москвы",
        "purpose": "строительство административного здания",
    },
]


def _xml_escape(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def _zip_bytes(entries: dict[str, bytes]) -> bytes:
    """Детерминированный zip: фиксированное время, без метаданных ОС."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 29, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o600 << 16
            archive.writestr(info, data)
    return buffer.getvalue()


def _docx_paragraph(text: str, *, bold: bool = False, size: int | None = None,
                    align: str | None = None) -> str:
    paragraph_props = f'<w:jc w:val="{align}"/>' if align else ""
    run_props = ""
    if bold or size:
        run_props = "<w:rPr>"
        if bold:
            run_props += "<w:b/>"
        if size:
            run_props += f'<w:sz w:val="{size}"/>'
        run_props += "</w:rPr>"
    return (
        f"<w:p><w:pPr>{paragraph_props}</w:pPr><w:r>{run_props}"
        f'<w:t xml:space="preserve">{_xml_escape(text)}</w:t></w:r></w:p>'
    )


def _docx_table(rows: list[list[str]]) -> str:
    borders = "".join(
        f'<w:{side} w:val="single" w:sz="4" w:space="0" w:color="808080"/>'
        for side in ("top", "left", "bottom", "right", "insideH", "insideV")
    )
    parts = [
        '<w:tbl><w:tblPr><w:tblW w:w="5000" w:type="pct"/>'
        f"<w:tblBorders>{borders}</w:tblBorders></w:tblPr>"
    ]
    for row in rows:
        parts.append("<w:tr>")
        for cell in row:
            parts.append(
                '<w:tc><w:tcPr><w:tcW w:w="2500" w:type="dxa"/></w:tcPr>'
                f"{_docx_paragraph(str(cell))}</w:tc>"
            )
        parts.append("</w:tr>")
    parts.append("</w:tbl>")
    return "".join(parts)


def _docx_bytes(title: str, blocks: list[tuple]) -> bytes:
    """Минимальный, но валидный DOCX: заголовок, абзацы, таблицы."""
    body = [_docx_paragraph(title, bold=True, size=32, align="center")]
    for block in blocks:
        if block[0] == "p":
            body.append(_docx_paragraph(block[1]))
        elif block[0] == "h":
            body.append(_docx_paragraph(block[1], bold=True, size=26))
        elif block[0] == "table":
            body.append(_docx_table(block[1]))
        elif block[0] == "br":
            body.append("<w:p/>")

    document = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
        f'<w:body>{"".join(body)}<w:sectPr/></w:body></w:document>'
    )
    content_types = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/>'
        '<Override PartName="/word/document.xml" '
        'ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
        "</Types>"
    )
    rels = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" '
        'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" '
        'Target="word/document.xml"/></Relationships>'
    )
    document_rels = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>'
    )
    return _zip_bytes(
        {
            "[Content_Types].xml": content_types.encode("utf-8"),
            "_rels/.rels": rels.encode("utf-8"),
            "word/document.xml": document.encode("utf-8"),
            "word/_rels/document.xml.rels": document_rels.encode("utf-8"),
        }
    )


def _pdf_text(x: float, y: float, size: float, text: str) -> str:
    escaped = text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    return f"BT /F1 {size} Tf {x} {y} Td ({escaped}) Tj ET"


def _pdf_bytes(pages: list[str], width: int = 842, height: int = 595) -> bytes:
    """Минимальный PDF с векторной графикой и текстом (латиница и цифры)."""
    page_ids: list[int] = []
    content_ids: list[int] = []
    next_id = 4
    for _ in pages:
        content_ids.append(next_id)
        next_id += 1
        page_ids.append(next_id)
        next_id += 1

    objects: list[bytes] = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        (
            f"<< /Type /Pages /Kids [{' '.join(f'{pid} 0 R' for pid in page_ids)}] "
            f"/Count {len(pages)} >>"
        ).encode("ascii"),
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    ]
    for index, content in enumerate(pages):
        data = content.encode("cp1252", errors="replace")
        objects.append(
            b"<< /Length " + str(len(data)).encode("ascii") + b" >>\nstream\n" + data + b"\nendstream"
        )
        objects.append(
            (
                f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] "
                f"/Resources << /Font << /F1 3 0 R >> >> /Contents {content_ids[index]} 0 R >>"
            ).encode("ascii")
        )

    output = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for index, obj in enumerate(objects, start=1):
        offsets.append(len(output))
        output += f"{index} 0 obj\n".encode("ascii") + obj + b"\nendobj\n"

    xref_offset = len(output)
    output += f"xref\n0 {len(objects) + 1}\n".encode("ascii")
    output += b"0000000000 65535 f \n"
    for offset in offsets[1:]:
        output += f"{offset:010d} 00000 n \n".encode("ascii")
    output += (
        f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\n"
        f"startxref\n{xref_offset}\n%%EOF\n"
    ).encode("ascii")
    return bytes(output)


def _pdf_cadastral_plan() -> bytes:
    """Кадастровый план территории: кварталы, участки, подписи, масштаб."""
    commands: list[str] = [
        "0.97 0.98 1 rg 0 0 842 595 re f",
        "0 0 0 RG 1 w 20 20 802 555 re S",
        _pdf_text(32, 556, 15, "CADASTRAL PLAN OF TERRITORY"),
        _pdf_text(32, 540, 8, "Cadastral quarter 77:01:0001002   |   IS REON demo data   |   Scale 1:2000"),
        "0.5 0.5 0.5 RG 0.6 w 32 532 m 810 532 l S",
    ]

    parcels = [
        ("77:01:0001002:5678", [(80, 150), (300, 150), (300, 330), (80, 330)], "1250 sq.m"),
        ("77:01:0001002:5679", [(320, 150), (520, 150), (520, 290), (320, 290)], "980 sq.m"),
        ("77:01:0001002:5680", [(540, 150), (760, 150), (760, 330), (540, 330)], "1420 sq.m"),
        ("77:01:0001002:5681", [(80, 360), (330, 360), (330, 500), (80, 500)], "1640 sq.m"),
        ("77:01:0001002:5682", [(350, 360), (600, 360), (600, 500), (350, 500)], "1310 sq.m"),
    ]
    for label, points, area in parcels:
        path = " ".join(
            f"{x} {y} {'m' if position == 0 else 'l'}"
            for position, (x, y) in enumerate(points)
        )
        commands.append("0.80 0.89 0.98 rg")
        commands.append(f"{path} h f")
        commands.append("0.10 0.40 0.75 RG 1.1 w")
        commands.append(f"{path} h S")
        commands.append(_pdf_text(points[0][0] + 6, points[0][1] + 8, 7, label))
        commands.append(_pdf_text(points[0][0] + 6, points[0][1] + 0, 7, area))

    # Северная стрелка.
    commands += [
        "0 0 0 RG 1 w 760 60 m 760 110 l S",
        "760 110 m 752 96 l 768 96 l h f",
        _pdf_text(754, 44, 8, "N"),
        # Линейный масштаб 1:2000: 100 м = 50 мм.
        "0 0 0 RG 0.8 w 80 70 m 330 70 l S",
        "80 65 m 80 75 l S 330 65 m 330 75 l S 205 65 m 205 75 l S",
        _pdf_text(74, 52, 7, "0"),
        _pdf_text(196, 52, 7, "100 m"),
        _pdf_text(324, 52, 7, "200 m"),
        _pdf_text(32, 28, 6, "Generated by IS REON for the demo stand. Numbers and coordinates are fictional."),
    ]
    return _pdf_bytes(["\n".join(commands)])


def _png_bytes(width: int, height: int, rows: list[bytearray]) -> bytes:
    raw = b"".join(b"\x00" + bytes(row) for row in rows)
    compressed = zlib.compress(raw, 6)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", compressed)
        + chunk(b"IEND", b"")
    )


def _png_photo(seed: int, width: int = 512, height: int = 384) -> bytes:
    """Фотофиксация здания: небо, фасад с окнами, газон, лёгкий шум."""
    rnd = random.Random(seed)
    noise = [rnd.randint(-9, 9) for _ in range(4096)]
    horizon = int(height * 0.62)
    building_left = int(width * 0.18)
    building_right = int(width * 0.82)
    building_top = int(height * 0.24)

    rows: list[bytearray] = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            if y < horizon:
                ratio = y / horizon
                red, green, blue = int(140 + 70 * ratio), int(185 + 50 * ratio), int(230 + 20 * ratio)
            else:
                red, green, blue = 118, 138, 104

            if building_left <= x <= building_right and building_top <= y <= horizon:
                red, green, blue = 206, 196, 176
                if y < building_top + 10:
                    red, green, blue = 128, 120, 112
                else:
                    window_x = (x - building_left) % 56
                    window_y = (y - building_top) % 44
                    if 12 <= window_x <= 42 and 12 <= window_y <= 30:
                        red, green, blue = 78, 112, 146

            shift = noise[(x * 7 + y * 13) % 4096]
            row += bytes(
                (
                    max(0, min(255, red + shift)),
                    max(0, min(255, green + shift)),
                    max(0, min(255, blue + shift)),
                )
            )
        rows.append(row)
    return _png_bytes(width, height, rows)


def _png_orthophoto(seed: int, width: int = 512, height: int = 384) -> bytes:
    """Ортофото квартала: дороги, газоны, контуры зданий, шум съёмки."""
    rnd = random.Random(seed)
    noise = [rnd.randint(-7, 7) for _ in range(4096)]
    buildings = [
        (60, 40, 110, 80),
        (300, 55, 130, 90),
        (70, 220, 120, 100),
        (330, 230, 110, 85),
    ]

    rows: list[bytearray] = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            red, green, blue = 148, 168, 118
            if 230 <= x <= 270 or 170 <= y <= 205:
                red, green, blue = 182, 182, 176
            for left, top, box_width, box_height in buildings:
                if left <= x <= left + box_width and top <= y <= top + box_height:
                    red, green, blue = 208, 202, 190
                    if x in (left, left + box_width) or y in (top, top + box_height):
                        red, green, blue = 110, 110, 104
            shift = noise[(x * 11 + y * 17) % 4096]
            row += bytes(
                (
                    max(0, min(255, red + shift)),
                    max(0, min(255, green + shift)),
                    max(0, min(255, blue + shift)),
                )
            )
        rows.append(row)
    return _png_bytes(width, height, rows)


def _registry_csv() -> str:
    rows = [
        "uno,cadastral_number,object_type,address,area_sqm,cadastral_value_rub,status,right"
    ]
    for obj in REGISTRY_OBJECTS:
        rows.append(
            f"{obj['uno']},{obj['cadastral']},{obj['type']},\"{obj['address']}\","
            f"{obj['area']:.1f},{obj['value']},{obj['status']},{obj['right']}"
        )
    return "\n".join(rows) + "\n"


def _address_csv() -> str:
    rows = ["uno,address,address_type,cadastral_number,registered_at"]
    for index, obj in enumerate(REGISTRY_OBJECTS):
        rows.append(
            f"{obj['uno']},\"{obj['address']}\","
            f"{'адрес здания' if obj['type'] == 'Здание' else 'адрес объекта'},"
            f"{obj['cadastral']},2026-10-{1 + index * 3:02d}"
        )
    return "\n".join(rows) + "\n"


def _tech_json() -> str:
    obj = REGISTRY_OBJECTS[0]
    return json.dumps(
        {
            "source": "МосгорБТИ",
            "uno": obj["uno"],
            "cadastralNumber": obj["cadastral"],
            "address": obj["address"],
            "characteristics": {
                "objectType": obj["type"],
                "purpose": obj["purpose"],
                "areaSqm": obj["area"],
                "floors": 9,
                "undergroundFloors": 1,
                "buildYear": 1998,
                "wallMaterial": "кирпич",
                "ceilingHeightM": 3.3,
                "cadastralValueRub": obj["value"],
            },
            "rights": [{"type": obj["right"], "registeredAt": "2019-05-20"}],
            "updatedAt": "2026-10-28T09:00:00+00:00",
        },
        ensure_ascii=False,
        indent=2,
    ) + "\n"


def _egrn_xml() -> str:
    objects = []
    for obj in REGISTRY_OBJECTS:
        objects.append(
            "  <Object>\n"
            f"    <Uno>{obj['uno']}</Uno>\n"
            f"    <CadastralNumber>{obj['cadastral']}</CadastralNumber>\n"
            f"    <ObjectType>{_xml_escape(obj['type'])}</ObjectType>\n"
            f"    <Address>{_xml_escape(obj['address'])}</Address>\n"
            f"    <Area>{obj['area']:.1f}</Area>\n"
            f"    <CadastralValue>{obj['value']}</CadastralValue>\n"
            f"    <Status>{_xml_escape(obj['status'])}</Status>\n"
            f"    <Right>{_xml_escape(obj['right'])}</Right>\n"
            "  </Object>"
        )
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<ReonEgrnExchange xmlns="urn:reon:egrn:exchange" generatedAt="2026-10-29T03:00:00+00:00">\n'
        + "\n".join(objects)
        + "\n</ReonEgrnExchange>\n"
    )


def _geojson() -> str:
    features = []
    for index, obj in enumerate(REGISTRY_OBJECTS):
        lon = 37.600 + index * 0.004
        lat = 55.750 + index * 0.003
        size = 0.0012
        features.append(
            {
                "type": "Feature",
                "geometry": {
                    "type": "Polygon",
                    "coordinates": [
                        [
                            [lon, lat],
                            [lon + size, lat],
                            [lon + size, lat + size],
                            [lon, lat + size],
                            [lon, lat],
                        ]
                    ],
                },
                "properties": {
                    "uno": obj["uno"],
                    "cadastralNumber": obj["cadastral"],
                    "objectType": obj["type"],
                    "address": obj["address"],
                    "areaSqm": obj["area"],
                    "status": obj["status"],
                },
            }
        )
    return json.dumps(
        {
            "type": "FeatureCollection",
            "name": "reon_objects_2026_10",
            "crs": {"type": "name", "properties": {"name": "urn:ogc:def:crs:OGC:1.3:CRS84"}},
            "features": features,
        },
        ensure_ascii=False,
        indent=2,
    ) + "\n"


def _monitoring_jsonl() -> str:
    kinds = ["фотофиксация", "обследование", "проверка адреса", "выявлено нарушение"]
    inspectors = ["Инспектор 1", "Инспектор 2", "Инспектор 3"]
    lines = []
    for index in range(300):
        obj = REGISTRY_OBJECTS[index % len(REGISTRY_OBJECTS)]
        lines.append(
            json.dumps(
                {
                    "eventId": f"mon-{index:04d}",
                    "occurredAt": f"2026-10-{1 + index % 28:02d}T{index % 24:02d}:{(index * 7) % 60:02d}:00+00:00",
                    "kind": kinds[index % len(kinds)],
                    "uno": obj["uno"],
                    "cadastralNumber": obj["cadastral"],
                    "inspector": inspectors[index % len(inspectors)],
                    "photoKey": (
                        f"object-media/photos/{obj['cadastral'].replace(':', '-')}/"
                        f"2026-10-{1 + index % 28:02d}/frame-{index % 12:02d}.png"
                    ),
                    "result": "нарушение" if index % 11 == 0 else "без замечаний",
                },
                ensure_ascii=False,
            )
        )
    return "\n".join(lines) + "\n"


def _docx_spec(title: str, blocks: list) -> dict:
    return {"kind": "docx", "title": title, "blocks": blocks}


def _vypiska_docx(obj: dict) -> dict:
    return _docx_spec(
        "Выписка из Реестра единых объектов недвижимости",
        [
            ("p", "Сформирована информационной системой ИС РЕОН. Демонстрационные данные."),
            ("br",),
            ("h", "Сведения об объекте"),
            (
                "table",
                [
                    ["Показатель", "Значение"],
                    ["Уникальный номер объекта (УНО)", obj["uno"]],
                    ["Кадастровый номер", obj["cadastral"]],
                    ["Вид объекта", obj["type"]],
                    ["Адрес", obj["address"]],
                    ["Площадь, кв. м", f"{obj['area']:.1f}".replace(".", ",")],
                    ["Кадастровая стоимость, руб.", f"{obj['value']:,}".replace(",", " ")],
                    ["Назначение", obj["purpose"]],
                    ["Статус записи", obj["status"]],
                    ["Вид права", obj["right"]],
                ],
            ),
            ("br",),
            ("h", "Сведения ЕГРН"),
            (
                "p",
                "Запись сопоставлена с ЕГРН по кадастровому номеру. "
                "Расхождений по площади, адресу и назначению не выявлено.",
            ),
            ("br",),
            ("p", "Выписка сформирована автоматически и не требует подписи."),
        ],
    )


def _tehplan_docx(obj: dict) -> dict:
    return _docx_spec(
        "Технический план объекта недвижимости",
        [
            ("p", "Подготовлен по данным МосгорБТИ. Демонстрационные данные."),
            ("br",),
            ("h", "Общие сведения"),
            (
                "table",
                [
                    ["Показатель", "Значение"],
                    ["Кадастровый номер", obj["cadastral"]],
                    ["Адрес", obj["address"]],
                    ["Вид объекта", obj["type"]],
                    ["Площадь, кв. м", f"{obj['area']:.1f}".replace(".", ",")],
                ],
            ),
            ("br",),
            ("h", "Технические характеристики"),
            (
                "table",
                [
                    ["Показатель", "Значение"],
                    ["Этажность", "9"],
                    ["Подземных этажей", "1"],
                    ["Год постройки", "1998"],
                    ["Материал стен", "кирпич"],
                    ["Высота потолков, м", "3,3"],
                ],
            ),
            ("br",),
            (
                "p",
                "Сведения внесены в реестр на основании обследования и данных "
                "адресного реестра зданий и сооружений.",
            ),
        ],
    )


def _akt_docx(obj: dict) -> dict:
    return _docx_spec(
        "Акт обследования объекта недвижимости",
        [
            ("p", "Плановый мониторинг территории. Демонстрационные данные."),
            ("br",),
            (
                "table",
                [
                    ["Показатель", "Значение"],
                    ["Кадастровый номер", obj["cadastral"]],
                    ["Адрес", obj["address"]],
                    ["Дата обследования", "21.10.2026"],
                    ["Основание", "план-график мониторинга ИС РЕОН"],
                    ["Исполнитель", "МосгорБТИ (демо)"],
                ],
            ),
            ("br",),
            ("h", "Заключение"),
            (
                "p",
                "Объект существует, характеристики соответствуют записям ЕГРН. "
                "Расхождений по площади и назначению не выявлено.",
            ),
            (
                "p",
                "Материалы фотофиксации приложены в бакете object-media.",
            ),
        ],
    )


def _mezhevanie_docx() -> dict:
    rows = [["Кадастровый номер", "Площадь, кв. м", "Вид права"]]
    for obj in REGISTRY_OBJECTS:
        rows.append(
            [obj["cadastral"], f"{obj['area']:.1f}".replace(".", ","), obj["right"]]
        )
    return _docx_spec(
        "Проект межевания территории квартала 77:01:0001001",
        [
            ("p", "Подготовлен для целей кадастрового учёта. Демонстрационные данные."),
            ("br",),
            ("h", "Перечень объектов"),
            ("table", rows),
            ("br",),
            (
                "p",
                "Проект определяет границы земельных участков и их частей. "
                "Сведения используются для изготовления кадастровых планов.",
            ),
        ],
    )


SCHEME_SVG = """<svg xmlns="http://www.w3.org/2000/svg" width="640" height="420" viewBox="0 0 640 420">
  <rect width="640" height="420" fill="#f8fafc"/>
  <text x="24" y="34" font-family="Segoe UI, Arial, sans-serif" font-size="17"
        fill="#101828">Схема расположения земельного участка</text>
  <text x="24" y="56" font-family="Segoe UI, Arial, sans-serif" font-size="12"
        fill="#475467">77:01:0001002:5678 · ул. Примерная, вл. 7</text>
  <rect x="40" y="80" width="260" height="180" fill="#d1e9ff" stroke="#2e90fa" stroke-width="2"/>
  <rect x="330" y="80" width="180" height="110" fill="#fef0c7" stroke="#f79009" stroke-width="2"/>
  <rect x="70" y="110" width="110" height="70" fill="#ffffff" stroke="#101828" stroke-width="2"/>
  <text x="40" y="286" font-family="Segoe UI, Arial, sans-serif" font-size="13"
        fill="#344054">ЗУ 1 250 кв. м</text>
  <text x="330" y="210" font-family="Segoe UI, Arial, sans-serif" font-size="13"
        fill="#344054">Здание 4 820,5 кв. м</text>
  <line x1="40" y1="330" x2="240" y2="330" stroke="#101828" stroke-width="1"/>
  <text x="40" y="352" font-family="Segoe UI, Arial, sans-serif" font-size="11"
        fill="#475467">Масштаб 1:2000</text>
</svg>
"""


# ---------------------------------------------------------------------------
# Описание демо-файлов файлового домена
# ---------------------------------------------------------------------------

FILE_SPECS = [
    {
        "name": "vypiska-reon-77-01-0001001-1234.docx",
        "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "description": "Выписка из ИС РЕОН по зданию 77:01:0001001:1234.",
        "tags": {"kind": "document", "documentType": "выписка", "object": "77:01:0001001:1234"},
        "content": _vypiska_docx(REGISTRY_OBJECTS[0]),
        "ageMinutes": 5,
    },
    {
        "name": "tehnicheskiy-plan-77-02-0003003-9012.docx",
        "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "description": "Технический план помещения 77:02:0003003:9012.",
        "tags": {"kind": "document", "documentType": "технический план", "object": "77:02:0003003:9012"},
        "content": _tehplan_docx(REGISTRY_OBJECTS[2]),
        "ageMinutes": 45,
    },
    {
        "name": "akt-obsledovaniya-77-03-0004004-3456.docx",
        "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "description": "Акт обследования сооружения 77:03:0004004:3456.",
        "tags": {"kind": "document", "documentType": "акт обследования", "object": "77:03:0004004:3456"},
        "content": _akt_docx(REGISTRY_OBJECTS[3]),
        "ageMinutes": 5 * 60,
    },
    {
        "name": "proekt-mezhevaniya-77-01-0001001.docx",
        "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "description": "Проект межевания квартала 77:01:0001001.",
        "tags": {"kind": "document", "documentType": "проект межевания"},
        "content": _mezhevanie_docx(),
        "ageMinutes": 24 * 60,
    },
    {
        "name": "kadastrovyy-plan-77-01-0001002.pdf",
        "contentType": "application/pdf",
        "description": "Кадастровый план территории квартала 77:01:0001002.",
        "tags": {"kind": "document", "documentType": "кадастровый план", "format": "pdf"},
        "content": {"kind": "pdf-plan"},
        "ageMinutes": 2 * 24 * 60,
    },
    {
        "name": "shema-zu-77-01-0001002-5678.svg",
        "contentType": "image/svg+xml",
        "description": "Схема расположения земельного участка 77:01:0001002:5678.",
        "tags": {"kind": "scheme", "object": "77:01:0001002:5678"},
        "content": {"kind": "text", "value": SCHEME_SVG},
        "ageMinutes": 3 * 24 * 60,
    },
    {
        "name": "fotofiksaciya-77-01-0001001-1234.png",
        "contentType": "image/png",
        "description": "Фотофиксация здания 77:01:0001001:1234, ракурс с улицы.",
        "tags": {"kind": "photo", "object": "77:01:0001001:1234", "survey": "2026-10-21"},
        "content": {"kind": "png-photo", "seed": 41},
        "ageMinutes": 4 * 24 * 60,
    },
    {
        "name": "fotofiksaciya-77-03-0004004-3456.png",
        "contentType": "image/png",
        "description": "Фотофиксация сооружения 77:03:0004004:3456.",
        "tags": {"kind": "photo", "object": "77:03:0004004:3456", "survey": "2026-10-22"},
        "content": {"kind": "png-photo", "seed": 43},
        "ageMinutes": 5 * 24 * 60,
    },
    {
        "name": "ortofoto-77-01-0001001-2026-10.png",
        "contentType": "image/png",
        "description": "Ортофото квартала 77:01:0001001 за октябрь 2026.",
        "tags": {"kind": "orthophoto", "quarter": "77:01:0001001"},
        "content": {"kind": "png-orthophoto", "seed": 53},
        "ageMinutes": 6 * 24 * 60,
    },
    {
        "name": "reestr-eon-2026-10.csv",
        "contentType": "text/csv",
        "description": "Выгрузка реестра ЕОН: объекты, площади, права.",
        "tags": {"kind": "export", "format": "csv", "period": "2026-10"},
        "content": {"kind": "text", "value": _registry_csv()},
        "ageMinutes": 7 * 24 * 60,
    },
    {
        "name": "reestr-eon-2026-10.geojson",
        "contentType": "application/geo+json",
        "description": "Пространственный слой объектов реестра (GeoJSON).",
        "tags": {"kind": "gis", "format": "geojson"},
        "content": {"kind": "text", "value": _geojson()},
        "ageMinutes": 8 * 24 * 60,
    },
    {
        "name": "adresnyy-reestr-2026-10.csv",
        "contentType": "text/csv",
        "description": "Адресный реестр зданий и сооружений (МосгорБТИ).",
        "tags": {"kind": "export", "source": "МосгорБТИ"},
        "content": {"kind": "text", "value": _address_csv()},
        "ageMinutes": 9 * 24 * 60,
    },
    {
        "name": "tehnichaskteristiki-77-01-0001001-1234.json",
        "contentType": "application/json",
        "description": "Технические характеристики здания из ИС МосгорБТИ.",
        "tags": {"kind": "data", "source": "МосгорБТИ", "object": "77:01:0001001:1234"},
        "content": {"kind": "text", "value": _tech_json()},
        "ageMinutes": 10 * 24 * 60,
    },
    {
        "name": "obmen-egrn-2026-10-29.xml",
        "contentType": "application/xml",
        "description": "Пакет обмена с ЕГРН за 29 октября 2026.",
        "tags": {"kind": "integration", "system": "ЕГРН"},
        "content": {"kind": "text", "value": _egrn_xml()},
        "ageMinutes": 12 * 24 * 60,
    },
    {
        "name": "monitoring-2026-10.jsonl",
        "contentType": "application/x-ndjson",
        "description": "Журнал мониторинга: фотофиксации, обследования, нарушения.",
        "tags": {"kind": "logs", "system": "мониторинг"},
        "content": {"kind": "text", "value": _monitoring_jsonl()},
        "ageMinutes": 15 * 24 * 60,
    },
    {
        "name": "arhiv-reestra-2026-10-29.zip",
        "contentType": "application/zip",
        "description": "Архив выгрузки реестра: CSV, GeoJSON и манифест.",
        "tags": {"kind": "archive", "period": "2026-10-29"},
        "content": {"kind": "zip", "entries": {
            "reestr-eon-2026-10.csv": {"kind": "text", "value": _registry_csv()},
            "reestr-eon-2026-10.geojson": {"kind": "text", "value": _geojson()},
            "manifest.json": {"kind": "text", "value": json.dumps(
                {"system": "ИС РЕОН", "period": "2026-10", "objects": len(REGISTRY_OBJECTS)},
                ensure_ascii=False, indent=2) + "\n"},
        }},
        "ageMinutes": 20 * 24 * 60,
    },
    {
        "name": "pending-egrn-import.xml",
        "contentType": "application/xml",
        "description": "Строка Pending: пакет ЕГРН уже в S3, сверка переведёт в Ready.",
        "tags": {"kind": "demo", "status": "Pending"},
        "content": {"kind": "text", "value": "<?xml version=\"1.0\"?>\n<egrn-import status=\"pending\"/>\n"},
        "status": "Pending",
        "ageMinutes": 2,
    },
    {
        "name": "failed-egrn-import.xml",
        "contentType": "application/xml",
        "description": "Строка Failed: импорт пакета ЕГРН не удался, объекта нет.",
        "tags": {"kind": "demo", "status": "Failed"},
        "content": {"kind": "text", "value": "<?xml version=\"1.0\"?>\n<egrn-import status=\"failed\"/>\n"},
        "status": "Failed",
        "uploaded": False,
        "ageMinutes": 2 * 24 * 60,
        "failureReason": "S3 upload failed for object 'files/failed-egrn-import.xml'.",
    },
    {
        "name": "missing-vypiska.pdf",
        "contentType": "application/pdf",
        "description": "Строка Missing: выписка пропала из S3.",
        "tags": {"kind": "demo", "status": "Missing"},
        "content": {"kind": "pdf-plan"},
        "status": "Missing",
        "uploaded": False,
        "ageMinutes": 3 * 24 * 60,
        "failureReason": "The object was not found in the object store during reconciliation.",
    },
    {
        "name": "corrupted-reestr-eon.csv",
        "contentType": "text/csv",
        "description": "Строка Corrupted: SHA-256 в Postgres не совпадает с объектом.",
        "tags": {"kind": "demo", "status": "Corrupted"},
        "content": {"kind": "text", "value": _registry_csv()},
        "status": "Corrupted",
        "dbSha256": "0" * 64,
        "ageMinutes": 5 * 24 * 60,
        "failureReason": "The stored object checksum does not match the metadata.",
    },
    {
        "name": "deleting-arhiv-2026-09.zip",
        "contentType": "application/zip",
        "description": "Строка Deleting: сверка повторит удаление объекта.",
        "tags": {"kind": "demo", "status": "Deleting"},
        "content": {"kind": "zip", "entries": {
            "README.txt": {"kind": "text", "value": "Архив за сентябрь 2026.\n"},
        }},
        "status": "Deleting",
        "ageMinutes": 60,
    },
    {
        "name": "deleted-fotofiksaciya.jpg",
        "contentType": "image/jpeg",
        "description": "Строка Deleted: soft delete, объекта в S3 нет.",
        "tags": {"kind": "demo", "status": "Deleted"},
        "content": {"kind": "png-photo", "seed": 47},
        "status": "Deleted",
        "uploaded": False,
        "ageMinutes": 10 * 24 * 60,
    },
]


# ---------------------------------------------------------------------------
# Демо-бакеты и объекты административного фасада
# ---------------------------------------------------------------------------

BUCKETS = [
    {"name": "registry-cases", "quotaBytes": 2 * 1024 * 1024 * 1024},
    {"name": "object-media", "quotaBytes": 500 * 1024 * 1024},
    {"name": "exports", "quotaBytes": 1024 * 1024 * 1024},
]

ADMIN_OBJECTS = [
    {
        "bucket": "registry-cases",
        "key": "README.md",
        "contentType": "text/markdown",
        "content": {
            "kind": "text",
            "value": (
                "# registry-cases\n\n"
                "Реестровые дела по объектам недвижимости: выписки, техпланы, "
                "акты обследования, схемы. Демонстрационные данные.\n"
            ),
        },
    },
    {
        "bucket": "registry-cases",
        "key": "77-01-0001001-1234/vypiska-reon.docx",
        "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "content": _vypiska_docx(REGISTRY_OBJECTS[0]),
    },
    {
        "bucket": "registry-cases",
        "key": "77-01-0001001-1234/tehnicheskiy-plan.docx",
        "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "content": _tehplan_docx(REGISTRY_OBJECTS[0]),
    },
    {
        "bucket": "registry-cases",
        "key": "77-01-0001001-1234/akt-obsledovaniya.docx",
        "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "content": _akt_docx(REGISTRY_OBJECTS[0]),
    },
    {
        "bucket": "registry-cases",
        "key": "77-01-0001002-5678/kadastrovyy-plan.pdf",
        "contentType": "application/pdf",
        "content": {"kind": "pdf-plan"},
    },
    {
        "bucket": "registry-cases",
        "key": "77-01-0001002-5678/shema-zu.svg",
        "contentType": "image/svg+xml",
        "content": {"kind": "text", "value": SCHEME_SVG},
    },
    {
        "bucket": "registry-cases",
        "key": "77-01-0001001-1234/",
        "contentType": "application/x-directory",
        "content": {"kind": "empty"},
    },
    {
        "bucket": "registry-cases",
        "key": "77-01-0001002-5678/",
        "contentType": "application/x-directory",
        "content": {"kind": "empty"},
    },
    {
        "bucket": "object-media",
        "key": "photos/77-01-0001001-1234/2026-10-21/facade.png",
        "contentType": "image/png",
        "content": {"kind": "png-photo", "seed": 41},
    },
    {
        "bucket": "object-media",
        "key": "photos/77-01-0001001-1234/2026-10-21/yard.png",
        "contentType": "image/png",
        "content": {"kind": "png-photo", "seed": 42},
    },
    {
        "bucket": "object-media",
        "key": "photos/77-03-0004004-3456/2026-10-22/facade.png",
        "contentType": "image/png",
        "content": {"kind": "png-photo", "seed": 43},
    },
    {
        "bucket": "object-media",
        "key": "orthophoto/77-01-0001001/2026-10.png",
        "contentType": "image/png",
        "content": {"kind": "png-orthophoto", "seed": 53},
    },
    {
        "bucket": "object-media",
        "key": "photos/",
        "contentType": "application/x-directory",
        "content": {"kind": "empty"},
    },
    {
        "bucket": "object-media",
        "key": "orthophoto/",
        "contentType": "application/x-directory",
        "content": {"kind": "empty"},
    },
    {
        "bucket": "exports",
        "key": "reestr-eon-2026-10.csv.gz",
        "contentType": "application/gzip",
        "content": {"kind": "gzip-text", "value": _registry_csv()},
    },
    {
        "bucket": "exports",
        "key": "obmen-egrn-2026-10-29.xml.gz",
        "contentType": "application/gzip",
        "content": {"kind": "gzip-text", "value": _egrn_xml()},
    },
    {
        "bucket": "exports",
        "key": "monitoring-2026-10.jsonl.gz",
        "contentType": "application/gzip",
        "content": {"kind": "gzip-text", "value": _monitoring_jsonl()},
    },
]


def _audit_events() -> list[dict]:
    """Записи журнала: те же действия, что пишут фасад и базовый API."""
    demo_file_id = str(uuid.uuid5(NAMESPACE, "file:vypiska-reon-77-01-0001001-1234.docx"))
    specs = [
        ("01", -7 * 24 * 60, "devops", "bucket.create", "registry-cases", "Success", 201,
         {"quotaBytes": 2 * 1024 * 1024 * 1024, "versioningEnabled": False}),
        ("02", -7 * 24 * 60 + 5, "devops", "bucket.create", "object-media", "Success", 201,
         {"quotaBytes": 500 * 1024 * 1024, "versioningEnabled": False}),
        ("03", -6 * 24 * 60, "admin", "folder.create", "registry-cases/77-01-0001001-1234/",
         "Success", 201, {"key": "77-01-0001001-1234/"}),
        ("04", -6 * 24 * 60 + 10, "admin", "object.upload",
         "registry-cases/77-01-0001001-1234/vypiska-reon.docx", "Success", 201,
         {"contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
          "cadastralNumber": "77:01:0001001:1234"}),
        ("05", -5 * 24 * 60, "frontend", "file.upload", f"file/{demo_file_id}", "Success", 201,
         {"size": 4_200, "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document"}),
        ("06", -5 * 24 * 60 + 20, "frontend", "file.metadata.update", f"file/{demo_file_id}",
         "Success", 200, {"changed": ["description", "tags"]}),
        ("07", -4 * 24 * 60, "admin", "object.batch", "object-media", "Failure", 500,
         {"uploaded": 0,
          "rolledBack": ["photos/77-01-0001001-1234/2026-10-21/facade.png",
                         "photos/77-01-0001001-1234/2026-10-21/yard.png"]}),
        ("08", -4 * 24 * 60 + 30, "admin", "object.batch", "object-media", "Success", 201,
         {"uploaded": 2}),
        ("09", -3 * 24 * 60, "system", "usage.refresh", "exports", "Success", 200,
         {"usedBytes": 180_000, "objectCount": 3}),
        ("10", -3 * 24 * 60 + 40, "admin", "object.tags.update",
         "registry-cases/77-01-0001002-5678/shema-zu.svg", "Success", 200,
         {"tags": {"kind": "scheme", "object": "77:01:0001002:5678"}}),
        ("11", -2 * 24 * 60, "frontend", "upload.begin",
         "file/019a7e4c-0000-7000-8000-000000000001", "Success", 201,
         {"partSize": 16_777_216}),
        ("12", -2 * 24 * 60 + 15, "frontend", "upload.complete",
         "file/019a7e4c-0000-7000-8000-000000000001", "Success", 201,
         {"parts": 3, "size": 50_331_648}),
        ("13", -36 * 60, "admin", "object.presign",
         "registry-cases/77-01-0001001-1234/vypiska-reon.docx", "Success", 200,
         {"expiresIn": 3600}),
        ("14", -26 * 60, "frontend", "file.copy", f"file/{demo_file_id}", "Success", 201,
         {"copyOf": demo_file_id}),
        ("15", -8 * 60, "admin", "object.delete",
         "object-media/photos/77-03-0004004-3456/2026-10-22/facade.png", "Failure", 502,
         {"reason": "S3 is not reachable"}),
        ("16", -5 * 60, "devops", "bucket.access.update", "object-media", "Success", 200,
         {"accessMode": "Private"}),
        ("17", -3 * 60, "system", "file.delete", f"file/{demo_file_id}", "Success", 204, {}),
        ("18", -1 * 60, "admin", "object.version.delete", "exports/reestr-eon-2026-10.csv.gz",
         "Success", 204, {"versionId": "v1"}),
    ]
    events = []
    for name, offset, actor, action, resource, result, status, details in specs:
        events.append(
            {
                "id": str(uuid.uuid5(NAMESPACE, "audit:" + name)),
                "occurredAt": NOW + timedelta(minutes=offset),
                "actor": actor,
                "action": action,
                "resource": resource,
                "result": result,
                "statusCode": status,
                "details": details,
            }
        )
    return events


# ---------------------------------------------------------------------------
# План
# ---------------------------------------------------------------------------

def _iso(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).isoformat(timespec="seconds")


def _extension(name: str) -> str:
    """Расширение по тем же правилам, что FileInput.GetSafeExtension."""
    extension = os.path.splitext(name)[1].lower()
    if not extension or not 2 <= len(extension) <= 16:
        return ""
    return extension if extension[1:].isalnum() else ""


def _materialize(spec: dict) -> bytes:
    kind = spec["kind"]
    if kind == "text":
        return spec["value"].encode("utf-8")
    if kind == "gzip-text":
        return gzip.compress(spec["value"].encode("utf-8"), mtime=0)
    if kind == "docx":
        return _docx_bytes(spec["title"], spec["blocks"])
    if kind == "pdf-plan":
        return _pdf_cadastral_plan()
    if kind == "png-photo":
        return _png_photo(spec["seed"])
    if kind == "png-orthophoto":
        return _png_orthophoto(spec["seed"])
    if kind == "zip":
        return _zip_bytes(
            {name: _materialize(entry) for name, entry in spec["entries"].items()}
        )
    if kind == "empty":
        return b""
    raise SystemExit(f"Неизвестный тип содержимого: {kind}")


def _file_entry(spec: dict) -> dict:
    name = spec["name"]
    file_id = uuid.uuid5(NAMESPACE, "file:" + name)
    body = _materialize(spec["content"])
    sha256 = hashlib.sha256(body).hexdigest()
    status = spec.get("status", "Ready")
    created_at = NOW - timedelta(minutes=spec.get("ageMinutes", 0))

    completed_at = None
    if status in {"Ready", "Missing", "Corrupted", "Deleting", "Deleted"}:
        completed_at = min(created_at + timedelta(minutes=2), NOW)

    deleted_at = created_at + timedelta(days=1) if status == "Deleted" else None

    return {
        "id": str(file_id),
        "bucket": MAIN_BUCKET,
        "key": f"{KEY_PREFIX}{file_id.hex}{_extension(name)}",
        "originalName": name,
        "contentType": spec["contentType"],
        "description": spec.get("description"),
        "tags": spec.get("tags"),
        "status": status,
        "uploaded": spec.get("uploaded", status in {"Ready", "Pending", "Deleting", "Corrupted"}),
        "size": len(body),
        "sha256": sha256,
        "dbSha256": spec.get("dbSha256", sha256),
        "failureReason": spec.get("failureReason"),
        "createdAt": _iso(created_at),
        "completedAt": _iso(completed_at) if completed_at else None,
        "deletedAt": _iso(deleted_at) if deleted_at else None,
        "content": spec["content"],
    }


def _object_entry(spec: dict) -> dict:
    body = _materialize(spec["content"])
    return {
        "bucket": spec["bucket"],
        "key": spec["key"],
        "contentType": spec["contentType"],
        "size": len(body),
        "sha256": hashlib.sha256(body).hexdigest(),
        "content": spec["content"],
    }


def build_plan() -> dict:
    return {
        "bucket": MAIN_BUCKET,
        "keyPrefix": KEY_PREFIX,
        "owner": OWNER,
        "files": [_file_entry(spec) for spec in FILE_SPECS],
        "buckets": BUCKETS,
        "objects": [_object_entry(spec) for spec in ADMIN_OBJECTS],
        "audit": [
            {
                "id": event["id"],
                "occurredAt": _iso(event["occurredAt"]),
                "actor": event["actor"],
                "action": event["action"],
                "resource": event["resource"],
                "result": event["result"],
                "statusCode": event["statusCode"],
                "details": event["details"],
            }
            for event in _audit_events()
        ],
    }


# ---------------------------------------------------------------------------
# SQL
# ---------------------------------------------------------------------------

def _str(value) -> str:
    if value is None:
        return "NULL"
    return "'" + str(value).replace("'", "''") + "'"


def _ts(value) -> str:
    if value is None:
        return "NULL"
    return _str(value) + "::timestamptz"


def _json(value) -> str:
    if value is None:
        return "NULL"
    return _str(json.dumps(value, ensure_ascii=False)) + "::jsonb"


def _uuid(value) -> str:
    return _str(value) + "::uuid"


def _insert_file(entry: dict, *, status: str, etag=None, version_id=None, pending_now=False) -> str:
    """INSERT строки files. Для шага Pending время берётся как now(), чтобы
    сверка не приняла только что созданную строку за зависшую."""
    columns = (
        "id, bucket, object_key, original_name, content_type, size, sha256, etag, "
        "s3_version_id, owner, description, tags, status, version, failure_reason, "
        "created_at, updated_at, completed_at, deleted_at"
    )
    created_at = "now()" if pending_now else _ts(entry["createdAt"])
    updated_at = "now()" if pending_now else _ts(entry["completedAt"] or entry["createdAt"])
    completed_at = None if status == "Pending" else entry["completedAt"]
    values = ", ".join(
        [
            _uuid(entry["id"]),
            _str(entry["bucket"]),
            _str(entry["key"]),
            _str(entry["originalName"]),
            _str(entry["contentType"]),
            str(entry["size"]),
            _str(entry["dbSha256"]),
            _str(etag),
            _str(version_id),
            _str(OWNER),
            _str(entry["description"]),
            _json(entry["tags"]),
            _str(status),
            "1",
            _str(entry["failureReason"] if status != "Ready" else None),
            created_at,
            updated_at,
            _ts(completed_at),
            _ts(entry["deletedAt"]),
        ]
    )
    return (
        f"INSERT INTO files ({columns})\nVALUES ({values})\n"
        "ON CONFLICT DO NOTHING;"
    )


def cmd_manifest() -> None:
    json.dump(build_plan(), sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")


def cmd_sql_pending() -> None:
    plan = json.load(sys.stdin)
    ready = [entry for entry in plan["files"] if entry["status"] == "Ready"]
    print("BEGIN;")
    print(f"-- Шаг 2: {len(ready)} строк files в статусе Pending.")
    for entry in ready:
        print(_insert_file(entry, status="Pending", pending_now=True))
    print("COMMIT;")


def cmd_sql_ready() -> None:
    plan = json.load(sys.stdin)
    ready = [entry for entry in plan["files"] if entry["status"] == "Ready"]
    print("BEGIN;")
    print(f"-- Шаг 4: перевод {len(ready)} строк в Ready по ETag из S3.")
    for entry in ready:
        version_id = entry.get("versionId") or None
        print(
            "UPDATE files SET status = 'Ready', etag = {etag}, s3_version_id = {version}, "
            "failure_reason = NULL, created_at = {created}, completed_at = {completed}, "
            "updated_at = {updated}\n"
            "WHERE id = {id} AND status IN ('Pending', 'Ready');".format(
                etag=_str(entry.get("etag")),
                version=_str(version_id),
                created=_ts(entry["createdAt"]),
                completed=_ts(entry["completedAt"] or entry["createdAt"]),
                updated=_ts(entry["completedAt"] or entry["createdAt"]),
                id=_uuid(entry["id"]),
            )
        )
    print("COMMIT;")


def cmd_sql_statuses() -> None:
    plan = build_plan()
    demo = [entry for entry in plan["files"] if entry["status"] != "Ready"]
    print("BEGIN;")
    print(f"-- Строки в остальных статусах жизненного цикла: {len(demo)}.")
    for entry in demo:
        print(_insert_file(entry, status=entry["status"]))
    print("COMMIT;")


def cmd_sql_admin() -> None:
    plan = build_plan()
    print("BEGIN;")
    print("-- Квоты демо-бакетов. Режим доступа остаётся как есть.")
    for bucket in plan["buckets"]:
        print(
            "INSERT INTO bucket_settings (bucket, quota_bytes, access_mode, created_at, updated_at)\n"
            "VALUES ({bucket}, {quota}, 'Private', {now}, {now})\n"
            "ON CONFLICT (bucket) DO UPDATE SET quota_bytes = EXCLUDED.quota_bytes, "
            "updated_at = EXCLUDED.updated_at;".format(
                bucket=_str(bucket["name"]),
                quota=str(bucket["quotaBytes"]),
                now=_ts(_iso(NOW)),
            )
        )
    print("-- Квота основного бакета (строку создаёт сам сервис при старте).")
    print(
        "INSERT INTO bucket_settings (bucket, quota_bytes, access_mode, created_at, updated_at)\n"
        "VALUES ({bucket}, {quota}, 'Private', {now}, {now})\n"
        "ON CONFLICT (bucket) DO UPDATE SET quota_bytes = EXCLUDED.quota_bytes, "
        "updated_at = EXCLUDED.updated_at;".format(
            bucket=_str(plan["bucket"]),
            quota=str(5 * 1024 * 1024 * 1024),
            now=_ts(_iso(NOW)),
        )
    )
    print(f"-- Журнал операций: {len(plan['audit'])} записей.")
    for event in plan["audit"]:
        print(
            "INSERT INTO audit_events (id, occurred_at, actor, action, resource, result, status_code, details)\n"
            "VALUES ({id}, {occurred}, {actor}, {action}, {resource}, {result}, {status}, {details})\n"
            "ON CONFLICT DO NOTHING;".format(
                id=_uuid(event["id"]),
                occurred=_ts(event["occurredAt"]),
                actor=_str(event["actor"]),
                action=_str(event["action"]),
                resource=_str(event["resource"]),
                result=_str(event["result"]),
                status=str(event["statusCode"]) if event["statusCode"] is not None else "NULL",
                details=_json(event["details"]),
            )
        )
    print("COMMIT;")


def cmd_sql_clean() -> None:
    plan = build_plan()
    file_ids = ", ".join(_uuid(entry["id"]) for entry in plan["files"])
    audit_ids = ", ".join(_uuid(event["id"]) for event in plan["audit"])
    admin_buckets = ", ".join(_str(bucket["name"]) for bucket in plan["buckets"])
    print("BEGIN;")
    print(f"DELETE FROM files WHERE id IN ({file_ids});")
    print(f"DELETE FROM audit_events WHERE id IN ({audit_ids});")
    print(f"DELETE FROM bucket_settings WHERE bucket IN ({admin_buckets});")
    print(
        "UPDATE bucket_settings SET quota_bytes = NULL, updated_at = now() "
        f"WHERE bucket = {_str(plan['bucket'])} AND quota_bytes = {5 * 1024 * 1024 * 1024};"
    )
    print("COMMIT;")


# ---------------------------------------------------------------------------
# S3
# ---------------------------------------------------------------------------

def _client():
    import boto3
    from botocore.client import Config

    return boto3.client(
        "s3",
        endpoint_url=S3_ENDPOINT,
        aws_access_key_id=S3_ACCESS_KEY,
        aws_secret_access_key=S3_SECRET_KEY,
        region_name=S3_REGION,
        config=Config(signature_version="s3v4", s3={"addressing_style": "path"}),
    )


def _ensure_bucket(client, bucket: str) -> None:
    from botocore.exceptions import ClientError

    try:
        client.head_bucket(Bucket=bucket)
        return
    except ClientError:
        pass

    try:
        client.create_bucket(Bucket=bucket)
        print(f"  создан бакет {bucket}", file=sys.stderr)
    except ClientError as exception:
        code = exception.response.get("Error", {}).get("Code", "")
        if code not in {"BucketAlreadyOwnedByYou", "BucketAlreadyExists"}:
            raise


def _upload(client, bucket: str, key: str, body: bytes, content_type: str, metadata: dict) -> dict:
    response = client.put_object(
        Bucket=bucket,
        Key=key,
        Body=body,
        ContentType=content_type,
        Metadata=metadata,
    )
    return {"etag": response.get("ETag"), "versionId": response.get("VersionId")}


def cmd_upload(scope: str) -> None:
    plan = json.load(sys.stdin)
    client = _client()

    targets: list[dict] = []
    if scope in {"files", "all"}:
        targets.extend(entry for entry in plan["files"] if entry["uploaded"])
    if scope in {"admin", "all"}:
        targets.extend(plan["objects"])

    for bucket in sorted({entry["bucket"] for entry in targets}):
        _ensure_bucket(client, bucket)

    for entry in targets:
        body = _materialize(entry["content"])
        sha256 = hashlib.sha256(body).hexdigest()
        if sha256 != entry["sha256"] or len(body) != entry["size"]:
            raise SystemExit(
                f"Содержимое {entry['key']} не совпало с планом: план {entry['size']}/{entry['sha256']}, "
                f"факт {len(body)}/{sha256}."
            )

        metadata = {}
        if "id" in entry:
            metadata = {"reon-file-id": entry["id"], "reon-sha256": sha256}

        try:
            result = _upload(client, entry["bucket"], entry["key"], body, entry["contentType"], metadata)
        except Exception as exception:  # noqa: BLE001 - сообщение важнее типа
            raise SystemExit(f"Не удалось загрузить {entry['bucket']}/{entry['key']}: {exception}")

        entry["etag"] = result["etag"]
        entry["versionId"] = result["versionId"]
        print(f"  загружен {entry['bucket']}/{entry['key']} ({entry['size']} Б)", file=sys.stderr)

    json.dump(plan, sys.stdout, ensure_ascii=False)


def cmd_clean_s3() -> None:
    from botocore.exceptions import ClientError

    plan = build_plan()
    client = _client()

    existing = {bucket["Name"] for bucket in client.list_buckets().get("Buckets", [])}

    removed = 0
    for entry in plan["files"] + plan["objects"]:
        if entry["bucket"] not in existing:
            continue
        try:
            client.delete_object(Bucket=entry["bucket"], Key=entry["key"])
            removed += 1
        except ClientError as exception:
            print(f"  не удалось удалить {entry['bucket']}/{entry['key']}: {exception}",
                  file=sys.stderr)

    print(f"  обработано объектов: {removed}", file=sys.stderr)

    for bucket in plan["buckets"]:
        name = bucket["name"]
        if name not in existing:
            continue
        try:
            # Бакет удаляем только если он пуст: чужие данные не трогаем.
            client.delete_bucket(Bucket=name)
            print(f"  удалён бакет {name}", file=sys.stderr)
        except ClientError as exception:
            print(f"  бакет {name} не удалён (не пуст или недоступен): {exception}",
                  file=sys.stderr)


# ---------------------------------------------------------------------------
# Точка входа
# ---------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    command = argv[1] if len(argv) > 1 else ""
    if command == "manifest":
        cmd_manifest()
    elif command == "sql-pending":
        cmd_sql_pending()
    elif command == "sql-ready":
        cmd_sql_ready()
    elif command == "sql-statuses":
        cmd_sql_statuses()
    elif command == "sql-admin":
        cmd_sql_admin()
    elif command == "sql-clean":
        cmd_sql_clean()
    elif command == "upload":
        cmd_upload(argv[2] if len(argv) > 2 else "all")
    elif command == "clean-s3":
        cmd_clean_s3()
    else:
        print(
            "Режимы: manifest, upload files|admin|all, sql-pending, sql-ready, "
            "sql-statuses, sql-admin, sql-clean, clean-s3",
            file=sys.stderr,
        )
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
