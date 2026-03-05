# FOLLOW_UPS.md

## Part 1 follow-up

- During Part 1 implementation, preprocessing was extended to forward-fill additional numeric columns (`Temperature`, `Windspeed`, `SolarIrradiation`) in addition to `Target` to tolerate sparse rows in the provided dataset.
- Follow-up action: validate this imputation strategy with stakeholders and decide whether these columns should remain forward-filled, become configurable per column, or use a different strategy.
