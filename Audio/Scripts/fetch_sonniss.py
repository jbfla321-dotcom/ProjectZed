# Downloads the chosen Sonniss GDC bundle files (royalty-free, no attribution required) from the
# archive.org mirrors of the official bundles (files are byte-identical to Sonniss's zips) into
# C:/Users/josht/SoundLibrary/GDC<year>/<library>/. Already-downloaded files are skipped.
import json, os, sys, urllib.parse, urllib.request

LIB = r"C:/Users/josht/SoundLibrary"
PICKS = {
    "2016": ["Audiobeast_Small_Room_H&K_416_5.56_Assault_Rifle_05m"],
    "2017": ["warfare_t3_mg_whizzes_ricochets_bullet_cracks", "bullet_flyby_fast_05", "bullet_impact_body_thump_02",
             "bullet_impact_concrete_brick_01", "bullet_impact_metal_heavy_08", "Impacts Hard - Short Wobbly Tail 01",
             "Impacts Soft - Short, Crack", "015_Foley_Footsteps_Asphalt_Boot", "169_Foley_Footsteps_Grass_Sneaker",
             "289_Foley_Footsteps_Rocks_Sneaker_Jump_Land",
             "Window,Small,Break,Medium Impact,Large Amount", "Glass,Shards,Smash,Medium Impact,Lots of Large", "SH101_Glass_Break_Pane_702T"],
    "2018": ["1879 - Footsteps - Wooden Stairs", "thunder_clap_04"],
    "2019": ["PM_SDNG_Single_Step_Footstep_19", "PM_SDNG_Single_Step_Footstep_46", "PM_SDNG_Stereo_Walk_Seamless_Loop_1",
             "S23_SFX_Footsteps_Gravel_Loafers_Loops_Walk_Normal", "RL_bodyfall_Dirt_M4_Close", "RL_bodyfall_Concrete_Generic_Feet",
             "HERO - CK - ARMOR Leather Walk", "Glass,Plate Glass,Thick,Break,Topple", "rain_shower of rain_steady rain_forest_leaves", "Thunder_Very-Close_Rain_08"],
    "2020": ["NAGANT 1m close front right shots", "NAGANT 50m distant front left shots", "NAGANT mixed long distance shot",
             "M1903A3, Firing, t2, 1m", "M1911A1, Firing, t2, 200m", "M1911A1, Handling, Dry Fire", "1928A1, Handling, Dry Fire",
             "P38, Handling, Removing Magazine", "MP 40, Handling, Cocking", "PM_BBI_Bullet_Impact_Dirt_3",
             "PM_BBI_Bullet_Impact_Hit_Body_Flesh_25", "Bullet Impact 16 - Light Version", "Bullet rock Impact 4",
             "PM_RI_Source_53", "PM_RI_Source_92", "PM_SDGS_113 ", "PM_SDGS_14 ", "PM_SDGS_186 ",
             "0014_Footsteps_water_puddle_single_splashes", "80s wooden door - 80s city apartment door", "ambient thunder clap distant with rain mono"],
    "2023": ["grass 3 single step 3", "mud 1 loop", "wooden floor 1 loop", "WOODImpt_Impact Wood 23", "glass_impact_break_002", "RAINVege_InsJ_Ambience_Rain_Moderate_02_LOOP", "THUN_InsJ_Thunder_Very-Close_Rain_03"],
    "2024": ["SVD Dragunov 7.62", "Lynx 50 - FIRING - Take 1 - 65m Behind", "Desert Eagle - VAR SFX - Magazine In and Out",
             "MECHMisc_Ricochet Hits 01", "DOORCreak_Wooden Door, Opening and Closing 09", "DOORCreak_Wooden Door, Door Slams"],
    "2026": ["FOLYClth_ClothMovement24", "FOLYClth_ClothMovement29", "RAINInt_Heavy Rain on Window,  Constant"],
}
INDEX = os.path.join(LIB, "_index")
manifest = []
for year, needles in PICKS.items():
    item = f"sonniss-gdc-{year}-game-audio-bundle-normalized"
    files = json.load(open(os.path.join(INDEX, f"files_{year}.json"), encoding="utf-8"))["result"]
    for needle in needles:
        hits = [f for f in files if f["name"].lower().endswith(".wav") and needle in f["name"]]
        if not hits:
            print("MISSING", year, needle); continue
        for f in hits:
            dest = os.path.join(LIB, f"GDC{year}", *f["name"].split("/"))
            manifest.append({"year": year, "path": f["name"], "local": dest})
            if os.path.exists(dest) and os.path.getsize(dest) == int(f["size"]): continue
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            url = f"https://archive.org/download/{item}/" + urllib.parse.quote(f["name"])
            print(f"GET {int(f['size'])/1e6:7.1f}MB {f['name']}", flush=True)
            urllib.request.urlretrieve(url, dest + ".part")
            os.replace(dest + ".part", dest)
json.dump(manifest, open(os.path.join(LIB, "manifest.json"), "w", encoding="utf-8"), indent=1, ensure_ascii=False)
print("done", len(manifest), "files")
