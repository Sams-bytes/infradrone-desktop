#!/usr/bin/env python3
import sys
from PIL import Image
import numpy as np

src = sys.argv[1]
dst = sys.argv[2]
img = Image.open(src)
arr = np.array(img).astype(float)
arr = ((arr - arr.min()) / (arr.max() - arr.min() + 1e-9) * 255).astype('uint8')
Image.fromarray(arr).save(dst)
