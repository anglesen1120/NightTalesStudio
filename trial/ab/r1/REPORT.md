# 02.1.3 A/B report (r1)

Run conditions: tier `standard` · effort `medium` · seed `20261001` · models gpt-6-luna vs gpt-6-sol · generated 2026-09-27 · prompt release `2026.10.1`

## Matrix summary

| story_id | run_id | outcome | json_valid | body_tokens | latency_ms | cost_usd |
|---|---|---|---|---|---|---|
| S01 | vi-true_crime-b7-luna-standard | ok | True | 1033 | 128031 | 0.00252 |
| S02 | vi-ghost-b3-luna-standard | ok | True | 688 | 135152 | 0.00146 |
| S03 | en-spiritual-b5-sol-standard | ok | True | 849 | 122348 | 0.02593 |
| S04 | vi-true_crime-b9-luna-standard | ok | True | 777 | 105954 | 0.00133 |
| S05 | vi-true_crime-b7-sol-standard | ok | True | 850 | 122032 | 0.02702 |
| S06 | vi-true_crime-b8-luna-standard | ok | True | 935 | 133892 | 0.00161 |
| S07 | en-true_crime-b9-sol-standard | ok | True | 604 | 88113 | 0.02350 |
| S08 | vi-true_crime-b9-sol-standard | ok | True | 756 | 107526 | 0.02625 |
| S09 | en-spiritual-b6-sol-standard | ok | True | 774 | 147448 | 0.03100 |
| S10 | en-ghost-b1-luna-standard | ok | True | 919 | 113104 | 0.00215 |
| S11 | vi-spiritual-b5-sol-standard | ok | True | 973 | 141869 | 0.03231 |
| S12 | en-ghost-b2-sol-standard | ok | True | 935 | 135723 | 0.02570 |
| S13 | en-true_crime-b9-luna-standard | ok | True | 657 | 103402 | 0.00125 |
| S14 | en-true_crime-b7-sol-standard | ok | True | 520 | 84398 | 0.02189 |
| S15 | vi-spiritual-b5-luna-standard | ok | True | 893 | 175774 | 0.00173 |
| S16 | en-spiritual-b5-luna-standard | ok | True | 652 | 85624 | 0.00103 |
| S17 | vi-ghost-b1-luna-standard | ok | True | 845 | 156148 | 0.00259 |
| S18 | en-spiritual-b6-luna-standard-r2 | ok | True | 830 | 131885 | 0.00143 |
| S19 | vi-spiritual-b6-luna-standard | ok | True | 1211 | 224832 | 0.00229 |
| S20 | en-true_crime-b7-luna-standard | ok | True | 632 | 97782 | 0.00218 |
| S21 | en-true_crime-b8-luna-standard | ok | True | 679 | 108768 | 0.00133 |
| S22 | en-ghost-b3-sol-standard | ok | True | 897 | 144566 | 0.02991 |
| S23 | en-ghost-b1-sol-standard | ok | True | 879 | 124232 | 0.02476 |
| S24 | vi-spiritual-b6-sol-standard | ok | True | 1310 | 148049 | 0.03370 |
| S25 | vi-ghost-b2-sol-standard | ok | True | 790 | 123725 | 0.02703 |
| S26 | en-ghost-b2-luna-standard | ok | True | 865 | 120030 | 0.00124 |
| S27 | vi-true_crime-b8-sol-standard | ok | True | 838 | 114311 | 0.02859 |
| S28 | vi-spiritual-b4-luna-standard | ok | True | 1249 | 173343 | 0.00280 |
| S29 | en-spiritual-b4-luna-standard | ok | True | 898 | 105174 | 0.00214 |
| S30 | en-ghost-b3-luna-standard | ok | True | 759 | 97567 | 0.00102 |
| S31 | vi-ghost-b1-sol-standard | ok | True | 899 | 157755 | 0.03282 |
| S32 | en-spiritual-b4-sol-standard | ok | True | 800 | 104604 | 0.04335 |
| S33 | vi-spiritual-b4-sol-standard | ok | True | 870 | 124206 | 0.04924 |
| S34 | vi-ghost-b3-sol-standard | ok | True | 1007 | 144186 | 0.03098 |
| S35 | vi-ghost-b2-luna-standard | ok | True | 682 | 127371 | 0.00236 |
| S36 | en-true_crime-b8-sol-standard | ok | True | 822 | 114701 | 0.02910 |

## Per-dimension means (n raters; `†` = n < 2)

| language | dimension | luna | sol |
|---|---|---|---|
| en | hook | 4.00 (n=2) | 3.83 (n=2) |
| en | pacing | 2.94 (n=2) | 3.00 (n=2) |
| en | atmosphere | 3.50 (n=2) | 3.39 (n=2) |
| en | natural_language | 3.67 (n=2) | 3.61 (n=2) |
| en | originality | 2.72 (n=2) | 2.94 (n=2) |
| en | length_fit | 1.67 (n=2) | 1.89 (n=2) |
| en | factual_care | 4.67 (n=2) | 4.83 (n=2) |
| vi | hook | 4.11 (n=2) | 4.06 (n=2) |
| vi | pacing | 3.17 (n=2) | 3.33 (n=2) |
| vi | atmosphere | 3.50 (n=2) | 3.61 (n=2) |
| vi | natural_language | 3.39 (n=2) | 3.56 (n=2) |
| vi | originality | 3.06 (n=2) | 3.11 (n=2) |
| vi | length_fit | 1.44 (n=2) | 1.33 (n=2) |
| vi | factual_care | 4.17 (n=2) | 4.00 (n=2) |

## Rater coverage and invalid cells

- S01 (vi-true_crime-b7-luna-standard): 2 AI rater(s), 0 native rater(s)
- S02 (vi-ghost-b3-luna-standard): 2 AI rater(s), 0 native rater(s)
- S03 (en-spiritual-b5-sol-standard): 2 AI rater(s), 0 native rater(s)
- S04 (vi-true_crime-b9-luna-standard): 2 AI rater(s), 0 native rater(s)
- S05 (vi-true_crime-b7-sol-standard): 2 AI rater(s), 0 native rater(s)
- S06 (vi-true_crime-b8-luna-standard): 2 AI rater(s), 0 native rater(s)
- S07 (en-true_crime-b9-sol-standard): 2 AI rater(s), 0 native rater(s)
- S08 (vi-true_crime-b9-sol-standard): 2 AI rater(s), 0 native rater(s)
- S09 (en-spiritual-b6-sol-standard): 2 AI rater(s), 0 native rater(s)
- S10 (en-ghost-b1-luna-standard): 2 AI rater(s), 0 native rater(s)
- S11 (vi-spiritual-b5-sol-standard): 2 AI rater(s), 0 native rater(s)
- S12 (en-ghost-b2-sol-standard): 2 AI rater(s), 0 native rater(s)
- S13 (en-true_crime-b9-luna-standard): 2 AI rater(s), 0 native rater(s)
- S14 (en-true_crime-b7-sol-standard): 2 AI rater(s), 0 native rater(s)
- S15 (vi-spiritual-b5-luna-standard): 2 AI rater(s), 0 native rater(s)
- S16 (en-spiritual-b5-luna-standard): 2 AI rater(s), 0 native rater(s)
- S17 (vi-ghost-b1-luna-standard): 2 AI rater(s), 0 native rater(s)
- S18 (en-spiritual-b6-luna-standard-r2): 2 AI rater(s), 0 native rater(s)
- S19 (vi-spiritual-b6-luna-standard): 2 AI rater(s), 0 native rater(s)
- S20 (en-true_crime-b7-luna-standard): 2 AI rater(s), 0 native rater(s)
- S21 (en-true_crime-b8-luna-standard): 2 AI rater(s), 0 native rater(s)
- S22 (en-ghost-b3-sol-standard): 2 AI rater(s), 0 native rater(s)
- S23 (en-ghost-b1-sol-standard): 2 AI rater(s), 0 native rater(s)
- S24 (vi-spiritual-b6-sol-standard): 2 AI rater(s), 0 native rater(s)
- S25 (vi-ghost-b2-sol-standard): 2 AI rater(s), 0 native rater(s)
- S26 (en-ghost-b2-luna-standard): 2 AI rater(s), 0 native rater(s)
- S27 (vi-true_crime-b8-sol-standard): 2 AI rater(s), 0 native rater(s)
- S28 (vi-spiritual-b4-luna-standard): 2 AI rater(s), 0 native rater(s)
- S29 (en-spiritual-b4-luna-standard): 2 AI rater(s), 0 native rater(s)
- S30 (en-ghost-b3-luna-standard): 2 AI rater(s), 0 native rater(s)
- S31 (vi-ghost-b1-sol-standard): 2 AI rater(s), 0 native rater(s)
- S32 (en-spiritual-b4-sol-standard): 2 AI rater(s), 0 native rater(s)
- S33 (vi-spiritual-b4-sol-standard): 2 AI rater(s), 0 native rater(s)
- S34 (vi-ghost-b3-sol-standard): 2 AI rater(s), 0 native rater(s)
- S35 (vi-ghost-b2-luna-standard): 2 AI rater(s), 0 native rater(s)
- S36 (en-true_crime-b8-sol-standard): 2 AI rater(s), 0 native rater(s)

Invalid / moderated cells (1): en-spiritual-b6-luna-standard (superseded by a rerun)

Rated cells with blocking validator issues (1, 02 T108): S19 (vi-spiritual-b6-luna-standard: stock_reveal_opener)

AI-rated (02 T106), not native-human evidence

## D15 decision

- `en`: luna_mean=3.083 · sol_mean=3.111 · sol_mean*0.9=2.800 · luna natural_language=3.667 (vi floor 3.5) · rule (a)=no (b)=no → **gpt-6-luna** (none)
- `vi`: luna_mean=3.111 · sol_mean=3.167 · sol_mean*0.9=2.850 · luna natural_language=3.389 (vi floor 3.5) · rule (a)=no (b)=fired → **gpt-6-sol** (b)

**verdict en: gpt-6-luna**
**verdict vi: gpt-6-sol**
