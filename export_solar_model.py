#!/usr/bin/env python3
"""Exports the already-trained solar classifier weights (/tmp/best_solar_model.pt)
to ONNX -- separate from training so we don't have to retrain just to fix
the export step."""
import json
import os
import torch
import torch.nn as nn

OUTPUT_ONNX = os.path.expanduser("~/infradrone-desktop/models/solar_defect_classifier.onnx")
OUTPUT_LABELS = os.path.expanduser("~/infradrone-desktop/models/solar_defect_labels.json")
IMG_SIZE = 40

classes = ['Cell', 'Cell-Multi', 'Cracking', 'Diode', 'Diode-Multi', 'Hot-Spot',
           'Hot-Spot-Multi', 'No-Anomaly', 'Offline-Module', 'Shadowing', 'Soiling', 'Vegetation']

class SolarCNN(nn.Module):
    def __init__(self, num_classes):
        super().__init__()
        self.features = nn.Sequential(
            nn.Conv2d(3, 32, 3, padding=1), nn.BatchNorm2d(32), nn.ReLU(), nn.MaxPool2d(2),
            nn.Conv2d(32, 64, 3, padding=1), nn.BatchNorm2d(64), nn.ReLU(), nn.MaxPool2d(2),
            nn.Conv2d(64, 128, 3, padding=1), nn.BatchNorm2d(128), nn.ReLU(), nn.AdaptiveAvgPool2d(1),
        )
        self.classifier = nn.Sequential(
            nn.Flatten(), nn.Dropout(0.3), nn.Linear(128, num_classes)
        )
    def forward(self, x):
        return self.classifier(self.features(x))

device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
model = SolarCNN(len(classes)).to(device)
model.load_state_dict(torch.load("/tmp/best_solar_model.pt", map_location=device))
model.eval()

os.makedirs(os.path.dirname(OUTPUT_ONNX), exist_ok=True)
dummy_input = torch.randn(1, 3, IMG_SIZE, IMG_SIZE).to(device)
torch.onnx.export(model, dummy_input, OUTPUT_ONNX,
                   input_names=["input"], output_names=["output"],
                   dynamic_axes={"input": {0: "batch"}, "output": {0: "batch"}})
with open(OUTPUT_LABELS, "w") as f:
    json.dump(classes, f, indent=2)

print(f"Exported model to: {OUTPUT_ONNX}")
print(f"Exported labels to: {OUTPUT_LABELS}")
