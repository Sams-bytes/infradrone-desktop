"""
Builds drone.glb: a VTOL QuadPlane in glTF axes.
  +X = nose (forward), +Y = up, +Z = right wing. Units: metres (Loong 2160 ~2.16 m span).
"""
import numpy as np, trimesh
from trimesh.visual.material import PBRMaterial
from trimesh.transformations import rotation_matrix, translation_matrix

def mat(rgba, metal=0.3, rough=0.45, blend=False):
    return PBRMaterial(baseColorFactor=rgba, metallicFactor=metal, roughnessFactor=rough,
                       alphaMode='BLEND' if blend else 'OPAQUE', doubleSided=blend)

BODY   = mat([0.16, 0.20, 0.26, 1.0], 0.5, 0.35)
WING   = mat([0.27, 0.32, 0.39, 1.0], 0.4, 0.4)
ACCENT = mat([0.13, 0.83, 0.93, 1.0], 0.1, 0.25)
GLASS  = mat([0.40, 0.90, 0.98, 1.0], 0.0, 0.05)
ROTOR  = mat([0.55, 0.92, 1.00, 0.35], 0.0, 0.2, blend=True)
HUB    = mat([0.05, 0.06, 0.08, 1.0], 0.6, 0.3)
RED    = mat([0.94, 0.27, 0.27, 1.0], 0.0, 0.3)
GREEN  = mat([0.13, 0.77, 0.37, 1.0], 0.0, 0.3)

scene = trimesh.Scene()
def add(m, material, name):
    m.visual = trimesh.visual.TextureVisuals(material=material)
    scene.add_geometry(m, node_name=name, geom_name=name)

Rz_to_x = rotation_matrix(np.pi/2, [0,1,0])   # Z-axis primitive -> along X
Rz_to_y = rotation_matrix(-np.pi/2, [1,0,0])  # Z-axis primitive -> along Y

# fuselage: capsule along X, slightly flattened
f = trimesh.creation.capsule(height=1.05, radius=0.11, count=[24, 24])
f.apply_transform(Rz_to_x); f.apply_scale([1.0, 0.85, 1.0])
add(f, BODY, 'fuselage')
# canopy / sensor glass near nose
c = trimesh.creation.icosphere(subdivisions=3, radius=0.09)
c.apply_scale([1.6, 0.8, 0.9]); c.apply_translation([0.45, 0.06, 0])
add(c, GLASS, 'canopy')
# spine accent
s = trimesh.creation.box(extents=[0.8, 0.012, 0.03]); s.apply_translation([-0.05, 0.095, 0])
add(s, ACCENT, 'spine')

# wing: thin box, slight taper via scaling tips is overkill; plain wing with accent edge
w = trimesh.creation.box(extents=[0.26, 0.025, 2.16]); w.apply_translation([0.05, 0.02, 0])
add(w, WING, 'wing')
le = trimesh.creation.box(extents=[0.02, 0.03, 2.12]); le.apply_translation([0.185, 0.025, 0])
add(le, ACCENT, 'leading_edge')

# booms + rotors
for side, z in (('L', -0.42), ('R', 0.42)):
    b = trimesh.creation.cylinder(radius=0.022, height=1.15, sections=20)
    b.apply_transform(Rz_to_x); b.apply_translation([0.0, 0.0, z])
    add(b, BODY, f'boom_{side}')
    for end, x in (('F', 0.55), ('A', -0.55)):
        m = trimesh.creation.cylinder(radius=0.045, height=0.08, sections=20)
        m.apply_transform(Rz_to_y); m.apply_translation([x, 0.05, z])
        add(m, HUB, f'motor_{side}{end}')
        d = trimesh.creation.cylinder(radius=0.30, height=0.006, sections=48)
        d.apply_transform(Rz_to_y); d.apply_translation([x, 0.1, z])
        add(d, ROTOR, f'rotor_{side}{end}')

# V-tail: two fins at the tail, tilted +-40 deg from horizontal
for side, sgn in (('L', -1), ('R', 1)):
    t = trimesh.creation.box(extents=[0.16, 0.012, 0.36])
    t.apply_translation([0, 0, sgn * 0.18])
    t.apply_transform(rotation_matrix(-np.radians(40) * sgn, [1, 0, 0]))
    t.apply_translation([-0.6, 0.03, 0])
    add(t, WING, f'vtail_{side}')

# pusher prop disc at the tail (axis along X)
p = trimesh.creation.cylinder(radius=0.15, height=0.006, sections=40)
p.apply_transform(Rz_to_x); p.apply_translation([-0.72, 0, 0])
add(p, ROTOR, 'pusher')

# nav lights: red port (left, -Z), green starboard (right, +Z)
for name, z, material in (('nav_red', -1.09, RED), ('nav_green', 1.09, GREEN)):
    l = trimesh.creation.icosphere(subdivisions=2, radius=0.03); l.apply_translation([0.05, 0.02, z])
    add(l, material, name)

scene.export('drone.glb')
print('bytes', len(open('drone.glb','rb').read()))
