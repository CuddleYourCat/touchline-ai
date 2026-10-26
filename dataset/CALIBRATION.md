# Calibration of the synthetic season

> Synthetic data. Every club and player is fictional, and every match is simulated from its seed.

The season is 38 matches between two fictional clubs, seeds 1 to 38, home and away alternating. The figures are per match, averaged over the season, and checked against the same ranges as `CalibrationTests`.

A season this size is a smaller sample than the 200 matches the ranges were agreed on. So a result is read two ways: **in range** when it sits inside the accepted bounds, and **within sampling error** when it sits outside them by no more than 3 standard errors. Anything else is **out of range**.

| Measure | Season | Accepted | Standard error | Result |
|---|---:|---|---:|---|
| goals per match | 2.21 | 2.2 to 3.4 | 0.214 | in range |
| shots per match | 21.50 | 20 to 30 | 0.742 | in range |
| share of shots on target | 0.32 | 0.28 to 0.4 | 0.014 | in range |
| passes per match | 854.71 | 750 to 1050 | 5.636 | in range |
| pass completion | 0.78 | 0.77 to 0.87 | 0.003 | in range |
| fouls per match | 18.05 | 16 to 26 | 0.837 | in range |
| yellow cards per match | 3.66 | 2.5 to 5 | 0.321 | in range |
| penalties per match | 0.16 | 0.1 to 0.5 | 0.060 | in range |
| red cards per match, including second yellows | 0.18 | 0.05 to 0.25 | 0.064 | in range |
| weak-foot shots as a share of footed shots | 0.19 | 0.15 to 0.3 | 0.018 | in range |
| headers as a share of all shots | 0.17 | 0.1 to 0.2 | 0.013 | in range |
| shots hitting the post or bar per match | 1.00 | 0.5 to 1.3 | 0.156 | in range |
| advantages played per match | 3.55 | 2 to 6 | 0.322 | in range |
| throw-ins per match | 39.21 | 35 to 50 | 0.722 | in range |
| goal kicks per match | 14.08 | 12 to 20 | 0.556 | in range |
| corners per match | 8.89 | 8 to 12 | 0.533 | in range |
| shots cleared off the line per match | 0.08 | 0.1 to 0.4 | 0.044 | within sampling error |
| total xG should track goals | 2.36 | within 0.4 of 2.21 | 0.180 | in range |
| xPass should track pass completion | 0.78 | within 0.02 of 0.78 | 0.002 | in range |

The numbers come from `SeasonAverages` and `CalibrationRanges`, the same code the calibration tests use. Regenerate with the export command in the README.
