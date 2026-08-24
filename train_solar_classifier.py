#!/usr/bin/env python3
"""
DAMbv InfraDrone — Solar Panel Thermal Anomaly Classifier Training
Trains a real CNN classifier on RaptorMaps' public InfraredSolarModules
dataset (20,000 real thermal images, 12 real defect classes) and exports
to ONNX for use in the app, matching the existing pothole/aerial model
pattern (onnx runtime inference).

Usage: python3 train_solar_classifier.py
"""
import json
import os
from collections import Counter
import numpy as np
from PIL import Image
import torch
import torch.nn as nn
from torch.utils.data import Dataset, DataLoader, random_split
from sklearn.model_selection import train_test_split

DATA_DIR = os.path.expanduser("~/solar_dataset/InfraredSolarModules/InfraredSolarModules")
METADATA_PATH = os.path.join(DATA_DIR, "module_metadata.json")
OUTPUT_ONNX = os.path.expanduser("~/infradrone-desktop/models/solar_defect_classifier.onnx")
OUTPUT_LABELS = os.path.expanduser("~/infradrone-desktop/models/solar_defect_labels.json")

BATCH_SIZE = 64
EPOCHS = 15
LR = 0.001
IMG_SIZE = 40  # pad/resize the 24x40 images to a consistent square-ish size

device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
print(f"Using device: {device}")

with open(METADATA_PATH) as f:
    metadata = json.load(f)

entries = list(metadata.values())
classes = sorted(set(e["anomaly_class"] for e in entries))
class_to_idx = {c: i for i, c in enumerate(classes)}
print(f"Classes ({len(classes)}): {classes}")

class SolarDataset(Dataset):
    def __init__(self, entries):
        self.entries = entries
    def __len__(self):
        return len(self.entries)
    def __getitem__(self, idx):
        e = self.entries[idx]
        img_path = os.path.join(DATA_DIR, e["image_filepath"])
        img = Image.open(img_path).convert("RGB").resize((IMG_SIZE, IMG_SIZE))
        arr = np.array(img, dtype=np.float32) / 255.0
        arr = arr.transpose(2, 0, 1)  # HWC -> CHW
        label = class_to_idx[e["anomaly_class"]]
        return torch.tensor(arr), label

train_entries, val_entries = train_test_split(
    entries, test_size=0.15, random_state=42,
    stratify=[e["anomaly_class"] for e in entries]
)
print(f"Train: {len(train_entries)}, Val: {len(val_entries)}")

train_ds = SolarDataset(train_entries)
val_ds = SolarDataset(val_entries)
train_loader = DataLoader(train_ds, batch_size=BATCH_SIZE, shuffle=True, num_workers=2)
val_loader = DataLoader(val_ds, batch_size=BATCH_SIZE, shuffle=False, num_workers=2)

# Real class imbalance handling: weight inversely proportional to frequency,
# so the model can't just always guess "No-Anomaly" and get 50% accuracy.
class_counts = Counter(e["anomaly_class"] for e in train_entries)
weights = torch.tensor([1.0 / class_counts[c] for c in classes], dtype=torch.float32).to(device)
weights = weights / weights.sum() * len(classes)

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

model = SolarCNN(len(classes)).to(device)
criterion = nn.CrossEntropyLoss(weight=weights)
optimizer = torch.optim.Adam(model.parameters(), lr=LR)

best_val_acc = 0.0
for epoch in range(EPOCHS):
    model.train()
    total_loss = 0
    for imgs, labels in train_loader:
        imgs, labels = imgs.to(device), labels.to(device)
        optimizer.zero_grad()
        out = model(imgs)
        loss = criterion(out, labels)
        loss.backward()
        optimizer.step()
        total_loss += loss.item()

    model.eval()
    correct, total = 0, 0
    with torch.no_grad():
        for imgs, labels in val_loader:
            imgs, labels = imgs.to(device), labels.to(device)
            out = model(imgs)
            pred = out.argmax(dim=1)
            correct += (pred == labels).sum().item()
            total += labels.size(0)
    val_acc = correct / total
    print(f"Epoch {epoch+1}/{EPOCHS}: loss={total_loss/len(train_loader):.4f}, val_acc={val_acc:.4f}")
    if val_acc > best_val_acc:
        best_val_acc = val_acc
        torch.save(model.state_dict(), "/tmp/best_solar_model.pt")

print(f"\nBest validation accuracy: {best_val_acc:.4f}")

# Export to ONNX
model.load_state_dict(torch.load("/tmp/best_solar_model.pt"))
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
