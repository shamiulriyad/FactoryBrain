"""Zone association: point in polygon, overlap rule, feet position, no zones."""
from app.detection.types import BBox
from app.zones.association import ZoneAssociator
from app.zones.geometry import point_in_polygon, polygon_area
from app.zones.types import Zone

SQUARE = ((0.1, 0.1), (0.5, 0.1), (0.5, 0.5), (0.1, 0.5))
L_SHAPE = ((0, 0), (0.6, 0), (0.6, 0.2), (0.2, 0.2), (0.2, 0.6), (0, 0.6))


def test_point_in_polygon_including_edges_and_concave_shapes():
    assert point_in_polygon((0.3, 0.3), SQUARE)
    assert point_in_polygon((0.1, 0.3), SQUARE)          # on an edge
    assert point_in_polygon((0.5, 0.5), SQUARE)          # on a corner
    assert not point_in_polygon((0.6, 0.3), SQUARE)
    assert point_in_polygon((0.1, 0.5), L_SHAPE)
    assert not point_in_polygon((0.4, 0.4), L_SHAPE)     # the notch of the L
    assert not point_in_polygon((0.3, 0.3), ((0, 0), (1, 1)))
    assert abs(polygon_area(SQUARE) - 0.16) < 1e-9


def test_overlapping_zones_resolve_to_the_smallest_then_by_name():
    line = Zone("a", "Line 1 area", "ProductionLine", ((0, 0), (1, 0), (1, 1), (0, 1)))
    ws = Zone("b", "T-025", "Workstation", SQUARE)
    twin = Zone("c", "A twin", "Workstation", SQUARE)
    assert ZoneAssociator([line, ws]).zone_for_point(0.3, 0.3).id == "b"
    assert ZoneAssociator([line, ws]).zone_for_point(0.8, 0.8).id == "a"
    assert ZoneAssociator([ws, twin, line]).zone_for_point(0.3, 0.3).id == "c"   # same area: name order
    assert ZoneAssociator([twin, ws, line]).zone_for_point(0.3, 0.3).id == "c"   # independent of input order


def test_a_person_is_placed_by_their_feet_not_their_head():
    ws = Zone("b", "T-025", "Workstation", SQUARE)
    z = ZoneAssociator([ws])
    head_in_feet_out = BBox(160, 100, 220, 300)          # feet at y = 300/360 = 0.83
    feet_in = BBox(160, 40, 220, 150)                    # feet at y = 150/360 = 0.42
    assert z.zone_for_box(head_in_feet_out, 640, 360) is None
    assert z.zone_for_box(feet_in, 640, 360).id == "b"


def test_without_zones_everyone_is_unassigned():
    assert ZoneAssociator([]).zone_for_point(0.5, 0.5) is None
    assert ZoneAssociator([Zone("x", "Bad", "Other", ((0, 0), (1, 1)))]).zones == []
