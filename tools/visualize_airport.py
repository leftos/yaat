"""Visualize OAK airport: raw GeoJSON features + runtime-generated graph nodes."""

import json
import pathlib

import matplotlib.pyplot as plt
import matplotlib.lines as mlines

GEOJSON_PATH = pathlib.Path(
    r"X:\dev\vzoa\training-files\atctrainer-airport-files\oak.geojson"
)
LAYOUT_PATH = pathlib.Path(__file__).parent / "oak_layout_dump.json"
OUTPUT_PATH = pathlib.Path(__file__).parent / "oak_airport_layout.png"

# Colors
C_RUNWAY = "#9999aa"
C_TAXIWAY = "#5599dd"
C_EDGE = "#667799"
C_PARKING = "#44cc55"
C_SPOT = "#eebb00"
C_INTERSECTION = "#8899aa"
C_HOLDSHORT = "#ff3333"
C_BG = "#0e0e1a"
C_TEXT = "#ccccdd"


def load_geojson():
    with open(GEOJSON_PATH, encoding="utf-8") as f:
        return json.load(f)


def load_layout():
    with open(LAYOUT_PATH, encoding="utf-8") as f:
        return json.load(f)


def plot_geojson_layer(ax, geojson):
    """Plot raw GeoJSON features as the base map layer."""
    for feature in geojson["features"]:
        props = feature["properties"]
        geom = feature["geometry"]
        ftype = props.get("type", "")

        if ftype == "runway":
            coords = geom["coordinates"]
            lons = [c[0] for c in coords]
            lats = [c[1] for c in coords]
            ax.plot(lons, lats, color=C_RUNWAY, linewidth=8, alpha=0.35,
                    solid_capstyle="butt", zorder=1)
            # Label at midpoint
            mid = len(coords) // 2
            ax.annotate(
                props.get("name", ""),
                (lons[mid], lats[mid]),
                color="#bbbbcc",
                fontsize=7,
                ha="center",
                va="bottom",
                fontweight="bold",
                zorder=10,
            )

        elif ftype == "taxiway":
            coords = geom["coordinates"]
            lons = [c[0] for c in coords]
            lats = [c[1] for c in coords]
            ax.plot(lons, lats, color=C_TAXIWAY, linewidth=0.8, alpha=0.5,
                    zorder=2)
            # Label at midpoint
            mid = len(coords) // 2
            name = props.get("name", "")
            if name:
                ax.annotate(
                    name,
                    (lons[mid], lats[mid]),
                    color=C_TAXIWAY,
                    fontsize=4,
                    ha="center",
                    va="center",
                    alpha=0.6,
                    zorder=10,
                )


def plot_layout_layer(ax, layout):
    """Plot computed graph nodes and edges from the parsed layout."""
    node_map = {}
    for node in layout["nodes"]:
        node_map[node["id"]] = node

    # Edges
    for edge in layout["edges"]:
        fr = node_map.get(edge["from"])
        to = node_map.get(edge["to"])
        if fr and to:
            ax.plot(
                [fr["lon"], to["lon"]],
                [fr["lat"], to["lat"]],
                color=C_EDGE,
                linewidth=0.25,
                alpha=0.25,
                linestyle="-",
                zorder=3,
            )

    # Separate nodes by type
    types = {
        "TaxiwayIntersection": {"lons": [], "lats": [], "c": C_INTERSECTION,
                                 "marker": "o", "s": 3, "z": 4},
        "Parking":             {"lons": [], "lats": [], "c": C_PARKING,
                                 "marker": "o", "s": 4, "z": 4},
        "Spot":                {"lons": [], "lats": [], "c": C_SPOT,
                                 "marker": "s", "s": 8, "z": 5},
        "RunwayHoldShort":     {"lons": [], "lats": [], "c": C_HOLDSHORT,
                                 "marker": "D", "s": 20, "z": 6},
    }

    for node in layout["nodes"]:
        g = types.get(node["type"])
        if g:
            g["lons"].append(node["lon"])
            g["lats"].append(node["lat"])

    for ntype, g in types.items():
        if g["lons"]:
            ax.scatter(
                g["lons"], g["lats"],
                c=g["c"], marker=g["marker"], s=g["s"],
                zorder=g["z"], edgecolors="none", alpha=0.9,
            )

    # Highlight hold-short nodes with a glow ring
    hs = types["RunwayHoldShort"]
    if hs["lons"]:
        ax.scatter(
            hs["lons"], hs["lats"],
            c="none", marker="D", s=60,
            zorder=5, edgecolors=C_HOLDSHORT, linewidths=0.5, alpha=0.4,
        )


def build_legend(ax):
    """Add a color-coded legend."""
    handles = [
        mlines.Line2D([], [], color=C_RUNWAY, linewidth=5, alpha=0.4,
                       label="Runway (GeoJSON)"),
        mlines.Line2D([], [], color=C_TAXIWAY, linewidth=1.5, alpha=0.5,
                       label="Taxiway (GeoJSON)"),
        mlines.Line2D([], [], color=C_EDGE, linewidth=1, alpha=0.4,
                       label="Graph Edge (computed)"),
        mlines.Line2D([], [], color=C_INTERSECTION, marker="o",
                       markersize=4, linestyle="None",
                       label="Taxiway Intersection"),
        mlines.Line2D([], [], color=C_PARKING, marker="o",
                       markersize=4, linestyle="None",
                       label="Parking"),
        mlines.Line2D([], [], color=C_SPOT, marker="s",
                       markersize=5, linestyle="None",
                       label="Spot"),
        mlines.Line2D([], [], color=C_HOLDSHORT, marker="D",
                       markersize=7, linestyle="None",
                       label="Hold-Short Node"),
    ]
    ax.legend(
        handles=handles,
        loc="upper left",
        fontsize=8,
        facecolor="#181830",
        edgecolor="#333355",
        labelcolor=C_TEXT,
        framealpha=0.95,
        borderpad=1,
        handletextpad=0.8,
    )


def main():
    geojson = load_geojson()
    layout = load_layout()

    fig, ax = plt.subplots(1, 1, figsize=(20, 16), dpi=150)
    fig.patch.set_facecolor(C_BG)
    ax.set_facecolor(C_BG)

    plot_geojson_layer(ax, geojson)
    plot_layout_layer(ax, layout)
    build_legend(ax)

    ax.set_aspect("equal")
    ax.tick_params(colors=C_TEXT, labelsize=7)
    for spine in ax.spines.values():
        spine.set_color("#333355")
    ax.set_title(
        "OAK Airport \u2014 GeoJSON + Computed Graph Nodes",
        color=C_TEXT,
        fontsize=14,
        pad=12,
    )
    ax.set_xlabel("Longitude", color=C_TEXT, fontsize=9)
    ax.set_ylabel("Latitude", color=C_TEXT, fontsize=9)

    # Add margin
    ax.margins(0.02)

    plt.tight_layout()
    fig.savefig(OUTPUT_PATH, facecolor=fig.get_facecolor())
    print(f"Saved to {OUTPUT_PATH}")
    plt.close(fig)


if __name__ == "__main__":
    main()
