#!/usr/bin/env python3
"""
DAMbv InfraDrone — Maintenance Ticket Generator
Usage: python3 generate_maintenance_ticket.py <input_json> <output_pdf>

input_json shape:
{
  "layer_name": "...",
  "fields": {"wegnummer": "N355", "vanhectometrering": "12", ...},
  "description": "...",
  "severity": "High",
  "lat": 53.21, "lon": 6.55,
  "timestamp": "2026-08-17 21:00"
}
"""
import sys, json
from datetime import datetime
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, Table,
                                 TableStyle, HRFlowable)
from reportlab.lib.enums import TA_CENTER, TA_LEFT

BRAND_BLUE = colors.HexColor('#1A4A8A')
BRAND_GREEN = colors.HexColor('#0D9E75')
BRAND_GRAY = colors.HexColor('#64748b')
LIGHT_GRAY = colors.HexColor('#f1f5f9')
CORAL = colors.HexColor('#D85A30')

SEVERITY_COLORS = {
    "Low": BRAND_GREEN,
    "Medium": colors.HexColor('#eab308'),
    "High": CORAL,
    "Critical": colors.HexColor('#dc2626'),
}

def generate(input_json_path, out_path):
    with open(input_json_path) as f:
        data = json.load(f)

    styles = getSampleStyleSheet()
    h1_style = ParagraphStyle('h1', parent=styles['Heading1'], textColor=BRAND_BLUE, fontSize=18)
    h2_style = ParagraphStyle('h2', parent=styles['Heading2'], textColor=BRAND_BLUE, fontSize=13)
    body_style = ParagraphStyle('body', parent=styles['Normal'], fontSize=10, leading=14)

    doc = SimpleDocTemplate(out_path, pagesize=A4,
                             topMargin=20*mm, bottomMargin=20*mm,
                             leftMargin=20*mm, rightMargin=20*mm)
    story = []

    story.append(Paragraph('DAMbv InfraDrone', h1_style))
    story.append(Paragraph('Maintenance Ticket — Drone Inspection Finding', styles['Normal']))
    story.append(HRFlowable(width="100%", thickness=1, color=BRAND_BLUE, spaceBefore=6, spaceAfter=12))

    severity = data.get('severity', 'Medium')
    sev_color = SEVERITY_COLORS.get(severity, BRAND_GRAY)
    story.append(Table([[
        Paragraph(f'<b>{severity.upper()}</b>', ParagraphStyle('sev', parent=body_style, textColor=colors.white, alignment=TA_CENTER, fontSize=12)),
    ]], colWidths=[170*mm], style=TableStyle([
        ('BACKGROUND', (0,0), (-1,-1), sev_color),
        ('TOPPADDING', (0,0), (-1,-1), 8), ('BOTTOMPADDING', (0,0), (-1,-1), 8),
    ])))
    story.append(Spacer(1, 12))

    story.append(Paragraph('Asset Reference (official province data)', h2_style))
    story.append(HRFlowable(width="100%", thickness=0.5, color=BRAND_GRAY, spaceAfter=6))
    story.append(Paragraph(f"Source layer: {data.get('layer_name', 'Unknown')}", body_style))
    story.append(Spacer(1, 4))

    fields = data.get('fields', {})
    if fields:
        rows = [['Field', 'Value']]
        for k, v in fields.items():
            rows.append([k, str(v)])
        story.append(Table(rows, colWidths=[70*mm, 100*mm], style=TableStyle([
            ('FONTSIZE', (0,0), (-1,-1), 9),
            ('BACKGROUND', (0,0), (-1,0), LIGHT_GRAY),
            ('TEXTCOLOR', (0,0), (-1,0), BRAND_GRAY),
            ('GRID', (0,0), (-1,-1), 0.5, colors.HexColor('#e2e8f0')),
            ('TOPPADDING', (0,0), (-1,-1), 4), ('BOTTOMPADDING', (0,0), (-1,-1), 4),
        ])))
    story.append(Spacer(1, 16))

    story.append(Paragraph('Inspection Finding', h2_style))
    story.append(HRFlowable(width="100%", thickness=0.5, color=BRAND_GRAY, spaceAfter=6))
    story.append(Paragraph(data.get('description', '(no description provided)'), body_style))
    story.append(Spacer(1, 12))

    lat, lon = data.get('lat'), data.get('lon')
    location_str = f"{lat:.6f}, {lon:.6f}" if lat and lon else "Not recorded"
    meta_rows = [
        ['Reported by', 'DAMbv InfraDrone (drone inspection)'],
        ['GPS Location', location_str],
        ['Timestamp', data.get('timestamp', datetime.now().strftime('%Y-%m-%d %H:%M'))],
    ]
    story.append(Table(meta_rows, colWidths=[50*mm, 120*mm], style=TableStyle([
        ('FONTSIZE', (0,0), (-1,-1), 9),
        ('TEXTCOLOR', (0,0), (0,-1), BRAND_GRAY),
        ('TOPPADDING', (0,0), (-1,-1), 3), ('BOTTOMPADDING', (0,0), (-1,-1), 3),
    ])))

    doc.build(story)
    print(f"Ticket generated: {out_path}")

if __name__ == "__main__":
    generate(sys.argv[1], sys.argv[2])
