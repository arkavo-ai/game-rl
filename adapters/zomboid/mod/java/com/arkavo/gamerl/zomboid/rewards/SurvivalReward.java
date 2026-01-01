package com.arkavo.gamerl.zomboid.rewards;

import zombie.characters.IsoPlayer;

import java.util.*;

/**
 * Multi-component reward calculator for survival scenarios.
 */
public class SurvivalReward {

    // Previous state tracking
    private int lastSurvivorCount = 0;
    private float lastAverageHealth = 1.0f;
    private int lastZombieKills = 0;
    private final Set<String> infectedSurvivors = new HashSet<>();

    /**
     * Compute reward components for current state.
     */
    public Map<String, Double> compute(List<IsoPlayer> survivors) {
        Map<String, Double> components = new HashMap<>();

        // === Critical: Life/Death ===
        int survivorDelta = survivors.size() - lastSurvivorCount;
        if (survivorDelta < 0) {
            components.put("survivor_death", survivorDelta * 100.0);
        } else if (survivorDelta > 0) {
            components.put("survivor_rescued", survivorDelta * 20.0);
        }

        // Check for new infections
        int newInfections = 0;
        for (IsoPlayer survivor : survivors) {
            String id = "Player" + survivor.getPlayerNum();
            try {
                if (survivor.getBodyDamage() != null &&
                    survivor.getBodyDamage().IsInfected() &&
                    !infectedSurvivors.contains(id)) {
                    newInfections++;
                    infectedSurvivors.add(id);
                }
            } catch (Exception e) {
                // ignore
            }
        }
        if (newInfections > 0) {
            components.put("survivor_infected", -newInfections * 50.0);
        }

        // === Needs: Homeostasis ===
        if (!survivors.isEmpty()) {
            float avgHunger = 0, avgThirst = 0, avgFatigue = 0, avgHealth = 0, avgStress = 0;
            int count = 0;

            for (IsoPlayer survivor : survivors) {
                try {
                    if (survivor.getStats() != null) {
                        avgHunger += survivor.getStats().getHunger();
                        avgThirst += survivor.getStats().getThirst();
                        avgFatigue += survivor.getStats().getFatigue();
                        avgStress += survivor.getStats().getStress();
                    }
                    avgHealth += survivor.getHealth();
                    count++;
                } catch (Exception e) {
                    // ignore
                }
            }

            if (count > 0) {
                avgHunger /= count;
                avgThirst /= count;
                avgFatigue /= count;
                avgHealth /= count;
                avgStress /= count;

                // Penalties for high need levels
                if (avgHunger > 0.3f) {
                    components.put("hunger", -avgHunger * 0.5);
                }
                if (avgThirst > 0.3f) {
                    components.put("thirst", -avgThirst * 0.5);
                }
                if (avgFatigue > 0.5f) {
                    components.put("fatigue", -(avgFatigue - 0.5) * 0.3);
                }
                if (avgHealth < 0.9f) {
                    components.put("health", (avgHealth - 1.0) * 0.3);
                }
                if (avgStress > 0.7f) {
                    components.put("stress", -avgStress * 0.2);
                }
            }
        }

        // === Combat ===
        int totalKills = 0;
        for (IsoPlayer survivor : survivors) {
            try {
                totalKills += survivor.getZombieKills();
            } catch (Exception e) {
                // ignore
            }
        }
        int killsDelta = totalKills - lastZombieKills;
        if (killsDelta > 0) {
            components.put("zombie_killed", killsDelta * 0.5);
        }

        // === Update tracking state ===
        lastSurvivorCount = survivors.size();
        if (!survivors.isEmpty()) {
            float sum = 0;
            for (IsoPlayer s : survivors) {
                sum += s.getHealth();
            }
            lastAverageHealth = sum / survivors.size();
        }
        lastZombieKills = totalKills;

        return components;
    }

    /**
     * Reset tracking state for new episode.
     */
    public void reset() {
        lastSurvivorCount = 0;
        lastAverageHealth = 1.0f;
        lastZombieKills = 0;
        infectedSurvivors.clear();
    }
}
