#!/usr/bin/env python3
"""
DAMbv InfraDrone — AI Defect Detection Report Generator
Usage: python3 generate_ai_report.py <manifest_json> <images_dir> <output_pdf>
"""
import sys, json, os
from datetime import datetime
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, Table,
                                 TableStyle, HRFlowable, Image, KeepTogether)
from reportlab.lib.enums import TA_CENTER, TA_LEFT

BRAND_BLUE = colors.HexColor('#1A4A8A')
BRAND_GREEN = colors.HexColor('#0D9E75')
BRAND_GRAY = colors.HexColor('#64748b')
LIGHT_GRAY = colors.HexColor('#f1f5f9')
CORAL = colors.HexColor('#D85A30')

def generate(json_path, images_dir, out_path):
    with open(json_path) as f:
        manifest = json.load(f)

    total_images = len(manifest)
    total_detections = sum(len(m['detections']) for m in manifest)
    flagged = sum(1 for m in manifest if len(m['detections']) > 0)
    all_conf = [d['confidence'] for m in manifest for d in m['detections']]
    avg_conf = (sum(all_conf) / len(all_conf) * 100) if all_conf else 0

    doc = SimpleDocTemplate(out_path, pagesize=A4,
                            leftMargin=20*mm, rightMargin=20*mm,
                            topMargin=20*mm, bottomMargin=20*mm)
    styles = getSampleStyleSheet()
    title_style = ParagraphStyle('title', fontSize=20, textColor=BRAND_BLUE,
                                  fontName='Helvetica-Bold')
    h2_style = ParagraphStyle('h2', fontSize=12, textColor=BRAND_BLUE,
                               fontName='Helvetica-Bold', spaceBefore=10, spaceAfter=4)
    small_style = ParagraphStyle('small', fontSize=8, textColor=BRAND_GRAY)
    fname_style = ParagraphStyle('fname', fontSize=10, fontName='Helvetica-Bold',
                                  textColor=colors.HexColor('#1e293b'))

    story = []
    story.append(Table([
        [Paragraph('<font color="#1A4A8A"><b>DAMbv BV</b></font>', styles['Normal']),
         Paragraph('<font color="#64748b">InfraDrone GCS — AI Defect Detection Report</font>', styles['Normal'])]
    ], colWidths=[90*mm, 80*mm], style=TableStyle([
        ('ALIGN', (1,0), (1,0), 'RIGHT'), ('VALIGN', (0,0), (-1,-1), 'MIDDLE'),
        ('BOTTOMPADDING', (0,0), (-1,-1), 8),
    ])))
    story.append(HRFlowable(width="100%", thickness=3, color=BRAND_BLUE, spaceAfter=10))
    story.append(Paragraph('POTHOLE DETECTION SUMMARY', title_style))
    story.append(Paragraph(datetime.now().strftime('%d %B %Y'),
                 ParagraphStyle('date', fontSize=12, textColor=BRAND_GRAY, spaceAfter=14)))

    stat_data = [[
        Paragraph(f'<b>{total_images}</b><br/><font size="8" color="#64748b">IMAGES ANALYZED</font>', styles['Normal']),
        Paragraph(f'<b>{total_detections}</b><br/><font size="8" color="#64748b">DETECTIONS</font>', styles['Normal']),
        Paragraph(f'<b>{avg_conf:.0f}%</b><br/><font size="8" color="#64748b">AVG CONFIDENCE</font>', styles['Normal']),
        Paragraph(f'<b>{flagged}</b><br/><font size="8" color="#64748b">IMAGES FLAGGED</font>', styles['Normal']),
    ]]
    story.append(Table(stat_data, colWidths=[42*mm]*4, style=TableStyle([
        ('BACKGROUND', (0,0), (-1,-1), LIGHT_GRAY),
        ('ALIGN', (0,0), (-1,-1), 'CENTER'), ('VALIGN', (0,0), (-1,-1), 'MIDDLE'),
        ('TOPPADDING', (0,0), (-1,-1), 10), ('BOTTOMPADDING', (0,0), (-1,-1), 10),
        ('FONTSIZE', (0,0), (-1,-1), 13),
    ])))
    story.append(Spacer(1, 16))

    story.append(Paragraph('Per-Image Breakdown', h2_style))
    story.append(HRFlowable(width="100%", thickness=1, color=BRAND_BLUE, spaceAfter=10))

    for m in manifest:
        block = []
        img_path = os.path.join(images_dir, m['annotated_image'])
        if os.path.exists(img_path):
            block.append(Image(img_path, width=160*mm, height=90*mm, kind='proportional'))
        block.append(Spacer(1, 4))
        block.append(Paragraph(m['filename'], fname_style))

        if m['detections']:
            rows = [['Detection', 'Confidence', 'Bounding box (x, y, w, h)']]
            for i, d in enumerate(m['detections'], 1):
                rows.append([
                    f"Pothole #{i}",
                    f"{d['confidence']*100:.0f}%",
                    f"{d['x']:.0f}, {d['y']:.0f}, {d['width']:.0f}, {d['height']:.0f}"
                ])
            block.append(Table(rows, colWidths=[40*mm, 30*mm, 70*mm], style=TableStyle([
                ('FONTSIZE', (0,0), (-1,-1), 9),
                ('TEXTCOLOR', (0,0), (-1,0), BRAND_GRAY),
                ('TEXTCOLOR', (1,1), (1,-1), CORAL),
                ('GRID', (0,0), (-1,-1), 0.5, colors.HexColor('#e2e8f0')),
                ('TOPPADDING', (0,0), (-1,-1), 4), ('BOTTOMPADDING', (0,0), (-1,-1), 4),
            ])))
        else:
            block.append(Paragraph('No potholes detected.', small_style))
        block.append(Spacer(1, 14))
        story.append(KeepTogether(block))

    story.append(HRFlowable(width="100%", thickness=1, color=BRAND_BLUE, spaceAfter=6))
    story.append(Paragraph(
        f'Generated by InfraDrone GCS — DAMbv BV — Groningen, Netherlands — {datetime.now().strftime("%Y-%m-%d %H:%M")}',
        small_style))
    story.append(Paragraph('CONFIDENTIAL — For operational and regulatory purposes only. DAMbv BV © 2026',
        ParagraphStyle('conf', fontSize=7, textColor=BRAND_GRAY, alignment=TA_CENTER)))

    doc.build(story)
    print(f"Report generated: {out_path}")

if __name__ == '__main__':
    if len(sys.argv) < 4:
        print("Usage: python3 generate_ai_report.py <manifest_json> <images_dir> <output_pdf>")
        sys.exit(1)
    generate(sys.argv[1], sys.argv[2], sys.argv[3])
