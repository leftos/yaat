# SFO Airport Ground Layout Reference

Auto-generated from `sfo.geojson` with manual verification. For use in E2E test development.

**Source**: `X:\dev\yaat-server\ArtccResources\ZOA\airports\sfo.geojson`
**Parser**: `GeoJsonParser.Parse("SFO", ...)` — parking connects via RAMP edge to nearest taxiway within 0.15nm.

## Key Layout Notes

- SFO has 4 runways: 28R/10L, 28L/10R, 1L/19R, 1R/19L
- **A** is the primary inner taxiway (lat 37.606-37.623), connecting to nearly everything
- **B** is the primary outer taxiway (lat 37.607-37.624), parallel to A
- **Z** is the cargo/north taxiway (lat 37.624-37.638)
- **C** is the international terminal taxiway (lat 37.612-37.631)
- **L** runs north-south (lat 37.605-37.627)
- **Y** connects terminals to the south end
- Terminal taxiways: T5/T5A/T5B, T6/T6A/T6B, T7/T7A/T7B, T8, T9 (connect A to terminal gates)
- Boarding area taxiways: T41E/T41W, T421-T424, SBE/SBW/SBC
- Variants: A1/A2, B1-B5, C2/C3, F1/F2, L2, M1-M5, Q1, S1-S3, Z1/Z2
- **77 taxiways total** — significantly more complex than OAK (34)

### Parking-to-Taxiway Connections (key groups)
| Parking Group | Nearest Taxiway | Distance |
|---------------|-----------------|----------|
| A gates (A1-A9) | M2 | ~0.10nm |
| B gates (B1-B9) | M5 | ~0.03nm |
| C gates (C10 etc) | T5B | ~0.05nm |
| D gates | T5A | ~0.08nm |
| E gates | T7B | ~0.05nm |
| F gates | T8 | ~0.07nm |
| G gates | B | ~0.06nm |
| 42-x gates | T422-T424 | ~0.02nm |
| 50-x (cargo) | Z | ~0.04nm |
| SBE gates | SBE | ~0.03nm |

## Summary
- **Parking spots**: 239
- **Spot nodes**: 33
- **Taxiways**: 77 (A, A1, A2, AF, AY1, AY2, AY3, AY4, B, B1, B2, B3, B4, B5, BC, C, C2, C3, CG, CZ, D, E, F, F1, F2, G, GL, H, K, L, L2, LF, M, M1, M2, M3, M4, M5, N, P, Q, Q1, R, S, S1, S2, S3, SBC, SBE, SBW, T, T41E, T41W, T421, T422, T423, T424, T5, T5A, T5B, T6, T6A, T6B, T7, T7A, T7B, T8, T9, U, UB1, UB2, V, Y, Z, Z1, Z2, ZS)
- **Runways**: 4

## Runways

### 10L - 28R
- 15 centerline points
- Lat: 37.613517 to 37.628719
- Lon: -122.393375 to -122.357130
- Endpoint 1 (first designator): lat=37.628719, lon=-122.393375
- Endpoint 2 (second designator): lat=37.613517, lon=-122.357130

### 10R - 28L
- 16 centerline points
- Lat: 37.611705 to 37.626270
- Lon: -122.393087 to -122.358361
- Endpoint 1 (first designator): lat=37.626270, lon=-122.393087
- Endpoint 2 (second designator): lat=37.611705, lon=-122.358361

### 1L - 19R
- 13 centerline points
- Lat: 37.607892 to 37.626448
- Lon: -122.382938 to -122.370641
- Endpoint 1 (first designator): lat=37.607892, lon=-122.382938
- Endpoint 2 (second designator): lat=37.626448, lon=-122.370641

### 1R - 19L
- 13 centerline points
- Lat: 37.606326 to 37.627306
- Lon: -122.381053 to -122.367135
- Endpoint 1 (first designator): lat=37.606326, lon=-122.381053
- Endpoint 2 (second designator): lat=37.627306, lon=-122.367135

## Taxiway Connectivity

| Taxiway | Lat Range | Lon Range | Points | Connects To |
|---------|-----------|-----------|--------|-------------|
| A | 37.6056�37.6227 | -122.3927�-122.3795 | 69 | A1, AF, AY1, AY2, AY3, AY4, B, B2, B3, B4, B5, BC, D, E, F, F1, G, H, K, L, M, M1, M2, Q, Q1, T, T5, T5A, T5B, T6, T6A, T6B, T7, T7A, T7B, T8, T9 |
| A1 | 37.6056�37.6070 | -122.3831�-122.3810 | 3 | A, A2, L, L2 |
| A2 | 37.6070�37.6080 | -122.3829�-122.3819 | 10 | A1, M1 |
| AF | 37.6165�37.6180 | -122.3797�-122.3777 | 11 | A, B, F |
| AY1 | 37.6129�37.6134 | -122.3829�-122.3818 | 8 | A, Y |
| AY2 | 37.6120�37.6123 | -122.3834�-122.3827 | 2 | A, Y |
| AY3 | 37.6103�37.6106 | -122.3845�-122.3839 | 2 | A, Y |
| AY4 | 37.6093�37.6103 | -122.3848�-122.3847 | 10 | A, Y |
| B | 37.6071�37.6235 | -122.3953�-122.3786 | 71 | A, AF, B1, B2, B3, B4, B5, D, E, F, F1, G, H, K, M, M1, M2, Q, Q1, T, T9, Z |
| B1 | 37.6227�37.6241 | -122.3908�-122.3893 | 13 | B, Q, Z |
| B2 | 37.6223�37.6230 | -122.3911�-122.3908 | 2 | A, B |
| B3 | 37.6215�37.6218 | -122.3924�-122.3916 | 2 | A, B |
| B4 | 37.6204�37.6208 | -122.3931�-122.3923 | 2 | A, B, B5, T8 |
| B5 | 37.6194�37.6213 | -122.3970�-122.3923 | 21 | A, B, B4, T8, T9 |
| BC | 37.6133�37.6141 | -122.3836�-122.3818 | 5 | A, Y |
| C | 37.6118�37.6306 | -122.3947�-122.3567 | 38 | C2, C3, CZ, D, E, F, K, L, N, P, R, SBE, SBW, T41E, T41W, T421, T422, T423, T424, U, Z |
| C2 | 37.6143�37.6155 | -122.3589�-122.3581 | 2 | C |
| C3 | 37.6287�37.6299 | -122.3933�-122.3925 | 2 | C, S1 |
| CG | 37.6318�37.6335 | -122.3916�-122.3891 | 14 | Z |
| CZ | 37.6303�37.6310 | -122.3939�-122.3937 | 7 | C, Z |
| D | 37.6205�37.6251 | -122.3841�-122.3811 | 6 | A, B, C |
| E | 37.6189�37.6269 | -122.3803�-122.3669 | 24 | A, B, C, F, L, T, V |
| F | 37.6105�37.6191 | -122.3807�-122.3585 | 36 | A, AF, B, C, E, F1, F2, L, LF, N, P |
| F1 | 37.6161�37.6169 | -122.3795�-122.3726 | 7 | A, B, F, L |
| F2 | 37.6109�37.6124 | -122.3611�-122.3601 | 2 | F |
| G | 37.6106�37.6142 | -122.3812�-122.3761 | 17 | A, B, GL, H, L |
| GL | 37.6123�37.6130 | -122.3770�-122.3747 | 15 | G, H, L |
| H | 37.6103�37.6124 | -122.3840�-122.3770 | 12 | A, B, G, GL, Y |
| K | 37.6215�37.6261 | -122.3865�-122.3835 | 5 | A, B, C, Q |
| L | 37.6050�37.6272 | -122.3831�-122.3658 | 39 | A, A1, C, E, F, F1, G, GL, L2, LF, M, V |
| L2 | 37.6058�37.6065 | -122.3810�-122.3794 | 2 | A1, L |
| LF | 37.6149�37.6156 | -122.3734�-122.3710 | 11 | F, L |
| M | 37.6073�37.6100 | -122.3851�-122.3785 | 12 | A, B, L, Y |
| M1 | 37.6080�37.6104 | -122.3901�-122.3828 | 25 | A, A2, B, M2, M3, M4, M5, Y |
| M2 | 37.6076�37.6136 | -122.3920�-122.3847 | 25 | A, B, M1, M3, M5 |
| M3 | 37.6092�37.6130 | -122.3887�-122.3865 | 16 | M1, M2 |
| M4 | 37.6102�37.6130 | -122.3887�-122.3871 | 13 | M1 |
| M5 | 37.6096�37.6134 | -122.3893�-122.3870 | 17 | M1, M2 |
| N | 37.6140�37.6179 | -122.3696�-122.3638 | 14 | C, F, P |
| P | 37.6148�37.6189 | -122.3696�-122.3662 | 13 | C, F, N |
| Q | 37.6225�37.6250 | -122.3901�-122.3843 | 20 | A, B, B1, K, Z |
| Q1 | 37.6223�37.6229 | -122.3883�-122.3880 | 2 | A, B |
| R | 37.6246�37.6286 | -122.3921�-122.3894 | 4 | C, Z |
| S | 37.6273�37.6285 | -122.3967�-122.3930 | 15 | S1, S2, S3, Z, ZS |
| S1 | 37.6277�37.6287 | -122.3939�-122.3933 | 2 | C3, S |
| S2 | 37.6273�37.6283 | -122.3930�-122.3923 | 2 | S, S3 |
| S3 | 37.6262�37.6273 | -122.3930�-122.3925 | 9 | S, S2, Z1 |
| SBC | 37.6241�37.6252 | -122.3790�-122.3764 | 2 | SBE, SBW |
| SBE | 37.6234�37.6268 | -122.3769�-122.3741 | 10 | C, SBC |
| SBW | 37.6245�37.6279 | -122.3795�-122.3772 | 8 | C, SBC |
| T | 37.6201�37.6223 | -122.3831�-122.3765 | 20 | A, B, E |
| T41E | 37.6253�37.6295 | -122.3816�-122.3788 | 12 | C |
| T41W | 37.6260�37.6287 | -122.3832�-122.3814 | 10 | C |
| T421 | 37.6267�37.6282 | -122.3848�-122.3837 | 5 | C |
| T422 | 37.6269�37.6278 | -122.3854�-122.3848 | 3 | C |
| T423 | 37.6275�37.6280 | -122.3867�-122.3864 | 2 | C |
| T424 | 37.6279�37.6284 | -122.3877�-122.3873 | 2 | C |
| T5 | 37.6154�37.6161 | -122.3821�-122.3805 | 4 | A |
| T5A | 37.6156�37.6162 | -122.3817�-122.3803 | 4 | A |
| T5B | 37.6152�37.6161 | -122.3826�-122.3806 | 5 | A |
| T6 | 37.6186�37.6197 | -122.3830�-122.3822 | 6 | A |
| T6A | 37.6181�37.6198 | -122.3836�-122.3824 | 9 | A |
| T6B | 37.6182�37.6196 | -122.3833�-122.3819 | 8 | A |
| T7 | 37.6195�37.6209 | -122.3860�-122.3851 | 5 | A |
| T7A | 37.6199�37.6211 | -122.3862�-122.3854 | 6 | A |
| T7B | 37.6194�37.6208 | -122.3858�-122.3848 | 6 | A |
| T8 | 37.6191�37.6204 | -122.3923�-122.3890 | 7 | A, B4, B5 |
| T9 | 37.6184�37.6205 | -122.3933�-122.3892 | 16 | A, B, B5 |
| U | 37.6298�37.6314 | -122.3928�-122.3921 | 4 | C, Z |
| UB1 | 37.6311�37.6354 | -122.3954�-122.3935 | 5 | Z |
| UB2 | 37.6330�37.6359 | -122.3924�-122.3915 | 14 | Z |
| V | 37.6231�37.6253 | -122.3733�-122.3680 | 5 | E, L |
| Y | 37.6089�37.6136 | -122.3858�-122.3825 | 22 | AY1, AY2, AY3, AY4, BC, H, M, M1 |
| Z | 37.6235�37.6382 | -122.3967�-122.3880 | 81 | B, B1, C, CG, CZ, Q, R, S, U, UB1, UB2, Z1, Z2, ZS |
| Z1 | 37.6252�37.6262 | -122.3936�-122.3930 | 2 | S3, Z |
| Z2 | 37.6248�37.6260 | -122.3989�-122.3948 | 18 | Z |
| ZS | 37.6285�37.6296 | -122.3966�-122.3956 | 9 | S, Z |

## Spot Nodes

| Name | Lat | Lon |
|------|-----|-----|
| 1 | 37.608952 | -122.385874 |
| 10 | 37.620176 | -122.392867 |
| 11 | 37.620436 | -122.393299 |
| 16 | 37.626517 | -122.395512 |
| 17 | 37.630551 | -122.395326 |
| 18 | 37.622333 | -122.374440 |
| 2 | 37.608301 | -122.386348 |
| 20 | 37.622612 | -122.389073 |
| 21 | 37.620934 | -122.385070 |
| 22 | 37.619152 | -122.380824 |
| 23 | 37.614832 | -122.380830 |
| 24 | 37.611445 | -122.383079 |
| 3 | 37.609815 | -122.387935 |
| 30 | 37.623187 | -122.388689 |
| 31 | 37.621513 | -122.384689 |
| 32 | 37.620710 | -122.382778 |
| 33 | 37.618820 | -122.379162 |
| 34 | 37.614529 | -122.380104 |
| 35 | 37.608377 | -122.383683 |
| 4 | 37.609150 | -122.388374 |
| 5 | 37.615623 | -122.380974 |
| 5A | 37.615796 | -122.380860 |
| 5B | 37.615456 | -122.381082 |
| 6 | 37.619292 | -122.382440 |
| 6A | 37.619383 | -122.382653 |
| 6B | 37.619202 | -122.382225 |
| 7 | 37.620512 | -122.385351 |
| 7A | 37.620634 | -122.385650 |
| 7B | 37.620403 | -122.385098 |
| 8 | 37.620179 | -122.391638 |
| 9 | 37.619586 | -122.392038 |
| I8L | 37.610573 | -122.359519 |
| I8R | 37.614690 | -122.356753 |

## Parking Areas

### 11-1 (1 spots)
- Names: 11-1
- Lat: 37.620038 to 37.620038
- Lon: -122.395964 to -122.395964
- Heading: 151
- Nearest taxiway: **B5** (0.0240nm)

### 11-2 (1 spots)
- Names: 11-2
- Lat: 37.620068 to 37.620068
- Lon: -122.395605 to -122.395605
- Heading: 151
- Nearest taxiway: **B5** (0.0407nm)

### 11-3 (1 spots)
- Names: 11-3
- Lat: 37.620329 to 37.620329
- Lon: -122.395096 to -122.395096
- Heading: 151
- Nearest taxiway: **B** (0.0411nm)

### 12-1 (1 spots)
- Names: 12-1
- Lat: 37.625036 to 37.625036
- Lon: -122.399031 to -122.399031
- Heading: 254
- Nearest taxiway: **Z2** (0.0255nm)

### 12-2 (1 spots)
- Names: 12-2
- Lat: 37.625429 to 37.625429
- Lon: -122.399170 to -122.399170
- Heading: 252
- Nearest taxiway: **Z2** (0.0256nm)

### 12-3 (1 spots)
- Names: 12-3
- Lat: 37.625820 to 37.625820
- Lon: -122.399269 to -122.399269
- Heading: 251
- Nearest taxiway: **Z2** (0.0233nm)

### 12-4 (1 spots)
- Names: 12-4
- Lat: 37.626235 to 37.626235
- Lon: -122.398835 to -122.398835
- Heading: 353
- Nearest taxiway: **Z2** (0.0125nm)

### 12-5 (1 spots)
- Names: 12-5
- Lat: 37.625830 to 37.625830
- Lon: -122.398261 to -122.398261
- Heading: 1
- Nearest taxiway: **Z2** (0.0241nm)

### 12-6 (1 spots)
- Names: 12-6
- Lat: 37.625544 to 37.625544
- Lon: -122.397787 to -122.397787
- Heading: 12
- Nearest taxiway: **Z2** (0.0409nm)

### 2-1 (1 spots)
- Names: 2-1
- Lat: 37.612142 to 37.612142
- Lon: -122.392995 to -122.392995
- Heading: 296
- Nearest taxiway: **M2** (0.0512nm)

### 2-2 (1 spots)
- Names: 2-2
- Lat: 37.612911 to 37.612911
- Lon: -122.392402 to -122.392402
- Heading: 325
- Nearest taxiway: **M2** (0.0451nm)

### 40-8 (1 spots)
- Names: 40-8
- Lat: 37.624737 to 37.624737
- Lon: -122.377895 to -122.377895
- Heading: 104
- Nearest taxiway: **SBC** (0.0604nm)

### 40-9 (1 spots)
- Names: 40-9
- Lat: 37.624354 to 37.624354
- Lon: -122.376985 to -122.376985
- Heading: 104
- Nearest taxiway: **SBC** (0.0302nm)

### 41-1 (1 spots)
- Names: 41-1
- Lat: 37.627010 to 37.627010
- Lon: -122.383654 to -122.383654
- Heading: 15
- Nearest taxiway: **T421** (0.0327nm)

### 41-10 (1 spots)
- Names: 41-10
- Lat: 37.628230 to 37.628230
- Lon: -122.380694 to -122.380694
- Heading: 96
- Nearest taxiway: **T41E** (0.0441nm)

### 41-11 (1 spots)
- Names: 41-11
- Lat: 37.626365 to 37.626365
- Lon: -122.381730 to -122.381730
- Heading: 321
- Nearest taxiway: **T41E** (0.0401nm)

### 41-12 (1 spots)
- Names: 41-12
- Lat: 37.627122 to 37.627122
- Lon: -122.381176 to -122.381176
- Heading: 284
- Nearest taxiway: **T41E** (0.0337nm)

### 41-13 (1 spots)
- Names: 41-13
- Lat: 37.627846 to 37.627846
- Lon: -122.380749 to -122.380749
- Heading: 284
- Nearest taxiway: **T41E** (0.0371nm)

### 41-15 (1 spots)
- Names: 41-15
- Lat: 37.629364 to 37.629364
- Lon: -122.379978 to -122.379978
- Heading: 284
- Nearest taxiway: **T41E** (0.0453nm)

### 41-16 (1 spots)
- Names: 41-16
- Lat: 37.629891 to 37.629891
- Lon: -122.379261 to -122.379261
- Heading: 306
- Nearest taxiway: **T41E** (0.0308nm)

### 41-18 (1 spots)
- Names: 41-18
- Lat: 37.626121 to 37.626121
- Lon: -122.380161 to -122.380161
- Heading: 104
- Nearest taxiway: **T41E** (0.0376nm)

### 41-19 (1 spots)
- Names: 41-19
- Lat: 37.626573 to 37.626573
- Lon: -122.379750 to -122.379750
- Heading: 104
- Nearest taxiway: **T41E** (0.0433nm)

### 41-2 (1 spots)
- Names: 41-2
- Lat: 37.627157 to 37.627157
- Lon: -122.383343 to -122.383343
- Heading: 284
- Nearest taxiway: **T41W** (0.0402nm)

### 41-20 (1 spots)
- Names: 41-20
- Lat: 37.627103 to 37.627103
- Lon: -122.379586 to -122.379586
- Heading: 104
- Nearest taxiway: **T41E** (0.0346nm)

### 41-21 (1 spots)
- Names: 41-21
- Lat: 37.627462 to 37.627462
- Lon: -122.378986 to -122.378986
- Heading: 104
- Nearest taxiway: **T41E** (0.0497nm)

### 41-22 (1 spots)
- Names: 41-22
- Lat: 37.628139 to 37.628139
- Lon: -122.378590 to -122.378590
- Heading: 104
- Nearest taxiway: **T41E** (0.0475nm)

### 41-23 (1 spots)
- Names: 41-23
- Lat: 37.628766 to 37.628766
- Lon: -122.378146 to -122.378146
- Heading: 104
- Nearest taxiway: **T41E** (0.0497nm)

### 41-3 (1 spots)
- Names: 41-3
- Lat: 37.627655 to 37.627655
- Lon: -122.383117 to -122.383117
- Heading: 284
- Nearest taxiway: **T421** (0.0380nm)

### 41-4 (1 spots)
- Names: 41-4
- Lat: 37.628130 to 37.628130
- Lon: -122.382802 to -122.382802
- Heading: 284
- Nearest taxiway: **T421** (0.0405nm)

### 41-5 (1 spots)
- Names: 41-5
- Lat: 37.628601 to 37.628601
- Lon: -122.382480 to -122.382480
- Heading: 284
- Nearest taxiway: **T41W** (0.0415nm)

### 41-6 (1 spots)
- Names: 41-6
- Lat: 37.629209 to 37.629209
- Lon: -122.382068 to -122.382068
- Heading: 295
- Nearest taxiway: **T41W** (0.0411nm)

### 41-7 (1 spots)
- Names: 41-7
- Lat: 37.626407 to 37.626407
- Lon: -122.382289 to -122.382289
- Heading: 284
- Nearest taxiway: **T41W** (0.0295nm)

### 41-8 (1 spots)
- Names: 41-8
- Lat: 37.626780 to 37.626780
- Lon: -122.381613 to -122.381613
- Heading: 104
- Nearest taxiway: **T41E** (0.0424nm)

### 41-9 (1 spots)
- Names: 41-9
- Lat: 37.627589 to 37.627589
- Lon: -122.381083 to -122.381083
- Heading: 95
- Nearest taxiway: **T41E** (0.0432nm)

### 42-1 (1 spots)
- Names: 42-1
- Lat: 37.627642 to 37.627642
- Lon: -122.385366 to -122.385366
- Heading: 104
- Nearest taxiway: **T422** (0.0170nm)

### 42-2 (1 spots)
- Names: 42-2
- Lat: 37.627913 to 37.627913
- Lon: -122.386050 to -122.386050
- Heading: 284
- Nearest taxiway: **T423** (0.0169nm)

### 42-3 (1 spots)
- Names: 42-3
- Lat: 37.628152 to 37.628152
- Lon: -122.386642 to -122.386642
- Heading: 104
- Nearest taxiway: **T423** (0.0154nm)

### 42-4 (1 spots)
- Names: 42-4
- Lat: 37.628325 to 37.628325
- Lon: -122.387052 to -122.387052
- Heading: 284
- Nearest taxiway: **T424** (0.0152nm)

### 42-5 (1 spots)
- Names: 42-5
- Lat: 37.628576 to 37.628576
- Lon: -122.387653 to -122.387653
- Heading: 104
- Nearest taxiway: **T424** (0.0171nm)

### 50-1 (1 spots)
- Names: 50-1
- Lat: 37.635025 to 37.635025
- Lon: -122.389984 to -122.389984
- Heading: 14
- Nearest taxiway: **Z** (0.0443nm)

### 50-2 (1 spots)
- Names: 50-2
- Lat: 37.635285 to 37.635285
- Lon: -122.389158 to -122.389158
- Heading: 2
- Nearest taxiway: **Z** (0.0452nm)

### 50-3 (1 spots)
- Names: 50-3
- Lat: 37.635655 to 37.635655
- Lon: -122.387061 to -122.387061
- Heading: 57
- Nearest taxiway: **Z** (0.0500nm)

### 50-4 (1 spots)
- Names: 50-4
- Lat: 37.636167 to 37.636167
- Lon: -122.387582 to -122.387582
- Heading: 48
- Nearest taxiway: **Z** (0.0434nm)

### 50-5 (1 spots)
- Names: 50-5
- Lat: 37.636626 to 37.636626
- Lon: -122.388009 to -122.388009
- Heading: 48
- Nearest taxiway: **Z** (0.0407nm)

### 50-6 (1 spots)
- Names: 50-6
- Lat: 37.637110 to 37.637110
- Lon: -122.388336 to -122.388336
- Heading: 47
- Nearest taxiway: **Z** (0.0422nm)

### 50-7 (1 spots)
- Names: 50-7
- Lat: 37.637617 to 37.637617
- Lon: -122.388730 to -122.388730
- Heading: 77
- Nearest taxiway: **Z** (0.0423nm)

### 50-8 (1 spots)
- Names: 50-8
- Lat: 37.638315 to 37.638315
- Lon: -122.389523 to -122.389523
- Heading: 78
- Nearest taxiway: **Z** (0.0301nm)

### 6-1 (1 spots)
- Names: 6-1
- Lat: 37.618565 to 37.618565
- Lon: -122.396197 to -122.396197
- Heading: 235
- Nearest taxiway: **B** (0.0440nm)

### 6-2 (1 spots)
- Names: 6-2
- Lat: 37.618202 to 37.618202
- Lon: -122.396021 to -122.396021
- Heading: 240
- Nearest taxiway: **B** (0.0445nm)

### 6-3 (1 spots)
- Names: 6-3
- Lat: 37.617822 to 37.617822
- Lon: -122.395900 to -122.395900
- Heading: 240
- Nearest taxiway: **B** (0.0441nm)

### 6-4 (1 spots)
- Names: 6-4
- Lat: 37.617479 to 37.617479
- Lon: -122.395630 to -122.395630
- Heading: 240
- Nearest taxiway: **B** (0.0406nm)

### 9-3 (1 spots)
- Names: 9-3
- Lat: 37.618998 to 37.618998
- Lon: -122.397384 to -122.397384
- Heading: 230
- Nearest taxiway: **B5** (0.0617nm)

### 9-4 (1 spots)
- Names: 9-4
- Lat: 37.619920 to 37.619920
- Lon: -122.397575 to -122.397575
- Heading: 243
- Nearest taxiway: **B5** (0.0517nm)

### 9-5 (1 spots)
- Names: 9-5
- Lat: 37.620974 to 37.620974
- Lon: -122.397663 to -122.397663
- Heading: 295
- Nearest taxiway: **B5** (0.0370nm)

### 9-8 (1 spots)
- Names: 9-8
- Lat: 37.621232 to 37.621232
- Lon: -122.396082 to -122.396082
- Heading: 60
- Nearest taxiway: **B5** (0.0396nm)

### 9-9 (1 spots)
- Names: 9-9
- Lat: 37.620587 to 37.620587
- Lon: -122.395165 to -122.395165
- Heading: 60
- Nearest taxiway: **B** (0.0566nm)

### A (16 spots)
- Names: 2-1A, 2-3A, 9-3A, 9-4A, 9-5A, 9-8A, 9-9A, A10, A11, A12, A15, A2, A4, A5, A8, A9
- Lat: 37.611244 to 37.621472
- Lon: -122.397603 to -122.388077
- Heading: 104
- Nearest taxiway: **M2** (0.1002nm)

### AR (1 spots)
- Names: A13R
- Lat: 37.611053 to 37.611053
- Lon: -122.389628 to -122.389628
- Heading: 322
- Nearest taxiway: **M5** (0.0454nm)

### AS (1 spots)
- Names: A6S
- Lat: 37.612843 to 37.612843
- Lon: -122.388482 to -122.388482
- Heading: 284
- Nearest taxiway: **M5** (0.0470nm)

### AT (3 spots)
- Names: A14T, A1T, A7T
- Lat: 37.611022 to 37.613935
- Lon: -122.390034 to -122.387561
- Heading: 2
- Nearest taxiway: **M5** (0.0478nm)

### AV (2 spots)
- Names: A13V, A1V
- Lat: 37.611046 to 37.614015
- Lon: -122.389718 to -122.387371
- Heading: 356
- Nearest taxiway: **M5** (0.0407nm)

### B (34 spots)
- Names: 2-1B, 2-2B, 2-2B, 9-3B, 9-4B, 9-5B, 9-8B, 9-9B, B1, B10, B11, B12, B13, B14, B15, B16, B17, B18, B19, B2, B20, B21, B22, B23, B24, B25, B26, B27, B4, B5, B6, B7, B8, B9
- Lat: 37.609971 to 37.621130
- Lon: -122.397512 to -122.383491
- Heading: 287
- Nearest taxiway: **M5** (0.0320nm)

### BS (4 spots)
- Names: B11S, B16S, B20S, B5S
- Lat: 37.611089 to 37.613354
- Lon: -122.386831 to -122.385834
- Heading: 55
- Nearest taxiway: **M3** (0.0344nm)

### BV (2 spots)
- Names: B23V, B27V
- Lat: 37.609949 to 37.610480
- Lon: -122.387270 to -122.386043
- Heading: 84
- Nearest taxiway: **M1** (0.0654nm)

### C (8 spots)
- Names: C10, C11, C3, C5, C6, C7, C8, C9
- Lat: 37.614259 to 37.615768
- Lon: -122.383200 to -122.381716
- Heading: 14
- Nearest taxiway: **T5B** (0.0539nm)

### CG (4 spots)
- Names: CG1, CG2, CG3, CG4
- Lat: 37.631856 to 37.633297
- Lon: -122.389633 to -122.389449
- Heading: 346
- Nearest taxiway: **CG** (0.0157nm)

### CR (1 spots)
- Names: C4R
- Lat: 37.614596 to 37.614596
- Lon: -122.383171 to -122.383171
- Heading: 14
- Nearest taxiway: **BC** (0.0386nm)

### CU (1 spots)
- Names: C4U
- Lat: 37.614631 to 37.614631
- Lon: -122.383185 to -122.383185
- Heading: 14
- Nearest taxiway: **BC** (0.0400nm)

### CV (1 spots)
- Names: C9V
- Lat: 37.615057 to 37.615057
- Lon: -122.381753 to -122.381753
- Heading: 255
- Nearest taxiway: **T5B** (0.0399nm)

### D (15 spots)
- Names: D1, D10, D11, D12, D14, D15, D16, D2, D3, D4, D5, D6, D7, D8, D9
- Lat: 37.616412 to 37.618617
- Lon: -122.382870 to -122.380352
- Heading: 345
- Nearest taxiway: **T5A** (0.0787nm)

### E (9 spots)
- Names: E1, E12, E2, E4, E5, E6, E7, E8, E9
- Lat: 37.618048 to 37.619942
- Lon: -122.386494 to -122.383555
- Heading: 272
- Nearest taxiway: **T7B** (0.0526nm)

### EK (2 spots)
- Names: E13K, E3K
- Lat: 37.618908 to 37.619803
- Lon: -122.385827 to -122.384077
- Heading: 218
- Nearest taxiway: **T7B** (0.0360nm)

### ET (2 spots)
- Names: E13T, E3T
- Lat: 37.618889 to 37.619757
- Lon: -122.385784 to -122.384034
- Heading: 203
- Nearest taxiway: **T7B** (0.0388nm)

### EU (2 spots)
- Names: E10U, E11U
- Lat: 37.619570 to 37.619632
- Lon: -122.384823 to -122.383535
- Heading: 248
- Nearest taxiway: **D** (0.0554nm)

### EV (2 spots)
- Names: E10V, E11V
- Lat: 37.619453 to 37.619471
- Lon: -122.384830 to -122.383485
- Heading: 284
- Nearest taxiway: **D** (0.0637nm)

### F (22 spots)
- Names: F1, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F2, F20, F21, F22, F3, F4, F5, F6, F7, F8, F9
- Lat: 37.617649 to 37.621542
- Lon: -122.390675 to -122.386413
- Heading: 284
- Nearest taxiway: **T8** (0.0714nm)

### FA (1 spots)
- Names: F3A
- Lat: 37.618582 to 37.618582
- Lon: -122.388056 to -122.388056
- Heading: 152
- Nearest taxiway: **T8** (0.0536nm)

### G (13 spots)
- Names: G1, G10, G103, G104, G105, G2, G3, G4, G5, G6, G7, G8, G9
- Lat: 37.616352 to 37.619157
- Lon: -122.394608 to -122.389599
- Heading: 15
- Nearest taxiway: **B** (0.0577nm)

### GR (2 spots)
- Names: G11R, G13R
- Lat: 37.618156 to 37.618827
- Lon: -122.394091 to -122.393855
- Heading: 104
- Nearest taxiway: **B** (0.0558nm)

### GS (2 spots)
- Names: G12S, G13S
- Lat: 37.618299 to 37.618955
- Lon: -122.393971 to -122.393883
- Heading: 143
- Nearest taxiway: **B** (0.0608nm)

### GT (2 spots)
- Names: G12T, G14T
- Lat: 37.618446 to 37.619165
- Lon: -122.394147 to -122.393839
- Heading: 150
- Nearest taxiway: **B** (0.0626nm)

### GV (1 spots)
- Names: G12V
- Lat: 37.618323 to 37.618323
- Lon: -122.393891 to -122.393891
- Heading: 74
- Nearest taxiway: **B** (0.0543nm)

### SAFETY (1 spots)
- Names: SAFETY
- Lat: 37.625779 to 37.625779
- Lon: -122.380517 to -122.380517
- Heading: 330
- Nearest taxiway: **T41E** (0.0360nm)

### SBE (8 spots)
- Names: SBE1, SBE2, SBE3, SBE4, SBE5, SBE6, SBE7, SBE8
- Lat: 37.624640 to 37.627131
- Lon: -122.375141 to -122.373716
- Heading: 284
- Nearest taxiway: **SBE** (0.0311nm)

### SBW (5 spots)
- Names: SBW1, SBW2, SBW3, SBW4, SBW5
- Lat: 37.626098 to 37.628337
- Lon: -122.379623 to -122.378091
- Heading: 284
- Nearest taxiway: **SBW** (0.0496nm)

### SIG (10 spots)
- Names: SIG1, SIG10, SIG2, SIG3, SIG4, SIG5, SIG6, SIG7, SIG8, SIG9
- Lat: 37.627031 to 37.628081
- Lon: -122.384687 to -122.383343
- Heading: 284
- Nearest taxiway: **T421** (0.0125nm)

### UB (5 spots)
- Names: UB1, UB2, UB3, UB4, UB5
- Lat: 37.632136 to 37.635658
- Lon: -122.394486 to -122.391407
- Heading: 82
- Nearest taxiway: **UB2** (0.0455nm)

### UNKN (1 spots)
- Names: UNKN
- Lat: 37.625751 to 37.625751
- Lon: -122.380506 to -122.380506
- Heading: 330
- Nearest taxiway: **T41E** (0.0367nm)
