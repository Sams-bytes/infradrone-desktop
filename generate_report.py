#!/usr/bin/env python3
"""
DAMbv InfraDrone — Flight Report Generator
Usage: python3 generate_report.py <csv_file> <output_pdf>
"""
import sys, csv, os
from datetime import datetime, timezone
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm, cm
from reportlab.lib import colors
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, HRFlowable
from reportlab.platypus import PageBreak
from reportlab.lib.enums import TA_CENTER, TA_LEFT, TA_RIGHT

# DAMbv brand colors
BRAND_BLUE = colors.HexColor('#1A4A8A')
BRAND_GREEN = colors.HexColor('#0D9E75')
BRAND_DARK = colors.HexColor('#0f1923')
BRAND_GRAY = colors.HexColor('#64748b')
LIGHT_GRAY = colors.HexColor('#f1f5f9')
RED = colors.HexColor('#ef4444')

def parse_csv(path):
    points = []
    with open(path) as f:
        reader = csv.DictReader(f)
        for row in reader:
            try:
                points.append({
                    'time': float(row['time']),
                    'lat': float(row['lat']),
                    'lon': float(row['lon']),
                    'alt': float(row['alt']),
                    'speed': float(row['speed']),
                    'heading': float(row['heading']),
                    'mode': row['mode'],
                    'armed': row['armed'] == 'True'
                })
            except: pass
    return points

def calc_stats(points):
    if not points: return {}
    armed = [p for p in points if p['armed']]
    start = datetime.fromtimestamp(points[0]['time'], tz=timezone.utc)
    end = datetime.fromtimestamp(points[-1]['time'], tz=timezone.utc)
    duration = end - start

    max_alt = max(p['alt'] for p in points)
    max_speed = max(p['speed'] for p in points)
    avg_speed = sum(p['speed'] for p in points) / len(points)

    # Total distance (haversine)
    import math
    def hav(lat1, lon1, lat2, lon2):
        R = 6371000
        dlat = math.radians(lat2-lat1)
        dlon = math.radians(lon2-lon1)
        a = math.sin(dlat/2)**2 + math.cos(math.radians(lat1))*math.cos(math.radians(lat2))*math.sin(dlon/2)**2
        return R * 2 * math.atan2(math.sqrt(a), math.sqrt(1-a))

    dist = sum(hav(points[i-1]['lat'], points[i-1]['lon'], points[i]['lat'], points[i]['lon'])
               for i in range(1, len(points)))

    modes = {}
    for p in points:
        modes[p['mode']] = modes.get(p['mode'], 0) + 1
    primary_mode = max(modes, key=modes.get) if modes else 'Unknown'

    return {
        'start': start.strftime('%Y-%m-%d %H:%M:%S UTC'),
        'end': end.strftime('%Y-%m-%d %H:%M:%S UTC'),
        'date': start.strftime('%d %B %Y'),
        'duration': str(duration).split('.')[0],
        'max_alt': f'{max_alt:.1f} m',
        'max_speed': f'{max_speed:.1f} m/s',
        'avg_speed': f'{avg_speed:.1f} m/s',
        'distance': f'{dist/1000:.2f} km',
        'points': len(points),
        'primary_mode': primary_mode,
        'start_lat': f'{points[0]["lat"]:.5f}',
        'start_lon': f'{points[0]["lon"]:.5f}',
    }

def generate(csv_path, out_path):
    points = parse_csv(csv_path)
    if not points:
        print("No data found in CSV")
        sys.exit(1)

    stats = calc_stats(points)
    doc = SimpleDocTemplate(out_path, pagesize=A4,
                            leftMargin=20*mm, rightMargin=20*mm,
                            topMargin=20*mm, bottomMargin=20*mm)

    styles = getSampleStyleSheet()
    title_style = ParagraphStyle('title', fontSize=22, textColor=BRAND_BLUE,
                                  fontName='Helvetica-Bold', alignment=TA_LEFT)
    h2_style = ParagraphStyle('h2', fontSize=13, textColor=BRAND_BLUE,
                               fontName='Helvetica-Bold', spaceBefore=12, spaceAfter=4)
    body_style = ParagraphStyle('body', fontSize=10, textColor=colors.HexColor('#333333'),
                                 leading=14)
    small_style = ParagraphStyle('small', fontSize=8, textColor=BRAND_GRAY)
    green_style = ParagraphStyle('green', fontSize=10, textColor=BRAND_GREEN,
                                  fontName='Helvetica-Bold')

    story = []

    # Header
    story.append(Table([
        [Paragraph('<font color="#1A4A8A"><b>DAMbv BV</b></font>', styles['Normal']),
         Paragraph(f'<font color="#64748b">InfraDrone GCS — Flight Report</font>', styles['Normal'])]
    ], colWidths=[90*mm, 80*mm], style=TableStyle([
        ('ALIGN', (0,0), (0,0), 'LEFT'),
        ('ALIGN', (1,0), (1,0), 'RIGHT'),
        ('VALIGN', (0,0), (-1,-1), 'MIDDLE'),
        ('BOTTOMPADDING', (0,0), (-1,-1), 8),
    ])))
    story.append(HRFlowable(width="100%", thickness=3, color=BRAND_BLUE, spaceAfter=10))

    # Title
    story.append(Paragraph('FLIGHT OPERATIONS REPORT', title_style))
    story.append(Paragraph(f'{stats["date"]}', ParagraphStyle('date', fontSize=14,
                           textColor=BRAND_GRAY, spaceAfter=16)))

    # Key stats boxes
    stat_data = [[
        Paragraph(f'<b>{stats["duration"]}</b><br/><font size="8" color="#64748b">DURATION</font>', styles['Normal']),
        Paragraph(f'<b>{stats["max_alt"]}</b><br/><font size="8" color="#64748b">MAX ALTITUDE</font>', styles['Normal']),
        Paragraph(f'<b>{stats["max_speed"]}</b><br/><font size="8" color="#64748b">MAX SPEED</font>', styles['Normal']),
        Paragraph(f'<b>{stats["distance"]}</b><br/><font size="8" color="#64748b">DISTANCE</font>', styles['Normal']),
    ]]
    story.append(Table(stat_data, colWidths=[42*mm]*4, style=TableStyle([
        ('BACKGROUND', (0,0), (-1,-1), LIGHT_GRAY),
        ('BOX', (0,0), (0,0), 1, BRAND_GREEN),
        ('BOX', (1,0), (1,0), 1, BRAND_BLUE),
        ('BOX', (2,0), (2,0), 1, BRAND_BLUE),
        ('BOX', (3,0), (3,0), 1, BRAND_BLUE),
        ('ALIGN', (0,0), (-1,-1), 'CENTER'),
        ('VALIGN', (0,0), (-1,-1), 'MIDDLE'),
        ('TOPPADDING', (0,0), (-1,-1), 10),
        ('BOTTOMPADDING', (0,0), (-1,-1), 10),
        ('FONTSIZE', (0,0), (-1,-1), 13),
        ('FONTNAME', (0,0), (-1,-1), 'Helvetica-Bold'),
    ])))
    story.append(Spacer(1, 16))

    # Flight details
    story.append(Paragraph('Flight Details', h2_style))
    story.append(HRFlowable(width="100%", thickness=1, color=BRAND_BLUE, spaceAfter=8))
    details = [
        ['Operator', 'DAMbv BV — Groningen, Netherlands'],
        ['Pilot', 'Ehetasham (Sam) Tahir'],
        ['EASA Licence', 'A1/A3 Open Category, A2 Open Category'],
        ['Aircraft', 'Loong 2160 VTOL — Cube Orange Flight Controller'],
        ['GCS Software', f'InfraDrone GCS v1.0 — DAMbv BV'],
        ['Flight start', stats['start']],
        ['Flight end', stats['end']],
        ['Duration', stats['duration']],
        ['Primary mode', stats['primary_mode']],
        ['Data points', str(stats['points'])],
        ['Home position', f'{stats["start_lat"]}°N, {stats["start_lon"]}°E'],
    ]
    story.append(Table(details, colWidths=[55*mm, 115*mm], style=TableStyle([
        ('BACKGROUND', (0,0), (0,-1), LIGHT_GRAY),
        ('FONTNAME', (0,0), (0,-1), 'Helvetica-Bold'),
        ('FONTSIZE', (0,0), (-1,-1), 9),
        ('GRID', (0,0), (-1,-1), 0.5, colors.HexColor('#e2e8f0')),
        ('TOPPADDING', (0,0), (-1,-1), 5),
        ('BOTTOMPADDING', (0,0), (-1,-1), 5),
        ('LEFTPADDING', (0,0), (-1,-1), 8),
    ])))
    story.append(Spacer(1, 16))

    # Performance summary
    story.append(Paragraph('Performance Summary', h2_style))
    story.append(HRFlowable(width="100%", thickness=1, color=BRAND_BLUE, spaceAfter=8))
    perf = [
        ['Metric', 'Value', 'Limit', 'Status'],
        ['Max altitude (AGL)', stats['max_alt'], '120 m (Open Cat.)', '✓ PASS' if float(stats['max_alt'].split()[0]) <= 120 else '✗ EXCEEDED'],
        ['Max speed', stats['max_speed'], 'No limit (Open Cat.)', '✓ N/A'],
        ['Total distance', stats['distance'], 'No limit (VLOS)', '✓ N/A'],
        ['Average speed', stats['avg_speed'], 'No limit', '✓ N/A'],
    ]
    story.append(Table(perf, colWidths=[55*mm, 40*mm, 55*mm, 20*mm], style=TableStyle([
        ('BACKGROUND', (0,0), (-1,0), BRAND_BLUE),
        ('TEXTCOLOR', (0,0), (-1,0), colors.white),
        ('FONTNAME', (0,0), (-1,0), 'Helvetica-Bold'),
        ('FONTSIZE', (0,0), (-1,-1), 9),
        ('GRID', (0,0), (-1,-1), 0.5, colors.HexColor('#e2e8f0')),
        ('BACKGROUND', (0,1), (-1,-1), LIGHT_GRAY),
        ('TOPPADDING', (0,0), (-1,-1), 5),
        ('BOTTOMPADDING', (0,0), (-1,-1), 5),
        ('LEFTPADDING', (0,0), (-1,-1), 8),
        ('TEXTCOLOR', (3,1), (3,-1), BRAND_GREEN),
        ('FONTNAME', (3,1), (3,-1), 'Helvetica-Bold'),
    ])))
    story.append(Spacer(1, 16))

    # Compliance statement
    story.append(Paragraph('Regulatory Compliance', h2_style))
    story.append(HRFlowable(width="100%", thickness=1, color=BRAND_BLUE, spaceAfter=8))
    story.append(Paragraph(
        'This flight was conducted in accordance with EU Regulation 2019/947 (UAS Regulation), '
        'EASA Open Category requirements, and DAMbv BV Operations Manual (DAMbv-OM-001). '
        'Airspace coordination was performed using InfraDrone GCS with official LVNL eAIP airspace data. '
        'Pre-flight checks were completed and signed off in InfraDrone GCS prior to operations.',
        body_style))
    story.append(Spacer(1, 16))

    # Signature block
    story.append(Paragraph('Sign-off', h2_style))
    story.append(HRFlowable(width="100%", thickness=1, color=BRAND_BLUE, spaceAfter=8))
    sig_data = [
        ['Remote Pilot', 'Date', 'Signature'],
        ['Ehetasham (Sam) Tahir', stats['date'], ''],
    ]
    story.append(Table(sig_data, colWidths=[65*mm, 45*mm, 60*mm], style=TableStyle([
        ('BACKGROUND', (0,0), (-1,0), BRAND_BLUE),
        ('TEXTCOLOR', (0,0), (-1,0), colors.white),
        ('FONTNAME', (0,0), (-1,0), 'Helvetica-Bold'),
        ('FONTSIZE', (0,0), (-1,-1), 9),
        ('GRID', (0,0), (-1,-1), 0.5, colors.HexColor('#e2e8f0')),
        ('BACKGROUND', (0,1), (-1,-1), colors.white),
        ('TOPPADDING', (0,0), (-1,-1), 8),
        ('BOTTOMPADDING', (0,0), (-1,-1), 20),
        ('LEFTPADDING', (0,0), (-1,-1), 8),
    ])))
    story.append(Spacer(1, 12))

    # Footer
    story.append(HRFlowable(width="100%", thickness=1, color=BRAND_BLUE, spaceAfter=6))
    story.append(Paragraph(
        f'Generated by InfraDrone GCS v1.0 — DAMbv BV — Groningen, Netherlands — {datetime.utcnow().strftime("%Y-%m-%d %H:%M UTC")}',
        small_style))
    story.append(Paragraph(
        'CONFIDENTIAL — For operational and regulatory purposes only. DAMbv BV © 2026',
        ParagraphStyle('conf', fontSize=7, textColor=BRAND_GRAY, alignment=TA_CENTER)))

    doc.build(story)
    print(f"Report generated: {out_path}")

if __name__ == '__main__':
    if len(sys.argv) < 3:
        print("Usage: python3 generate_report.py <csv_file> <output_pdf>")
        sys.exit(1)
    generate(sys.argv[1], sys.argv[2])
