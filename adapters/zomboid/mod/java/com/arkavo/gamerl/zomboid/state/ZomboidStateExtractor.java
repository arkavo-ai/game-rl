package com.arkavo.gamerl.zomboid.state;

import zombie.characters.IsoPlayer;
import zombie.characters.IsoZombie;
import zombie.iso.IsoWorld;
import zombie.iso.IsoCell;
import zombie.iso.IsoGridSquare;
import zombie.iso.IsoObject;
import zombie.iso.objects.IsoWorldInventoryObject;
import zombie.core.Rand;

import java.security.MessageDigest;
import java.util.*;

/**
 * Extracts game state for observations.
 */
public class ZomboidStateExtractor {

    private final Map<String, Map<String, Object>> previousSurvivorStates = new HashMap<>();

    /**
     * Extract full observation for given survivors.
     */
    public Map<String, Object> extractObservation(List<IsoPlayer> survivors, boolean fullState) {
        Map<String, Object> obs = new HashMap<>();

        // Tick/time info
        obs.put("Tick", getCurrentTick());
        obs.put("GameTime", extractGameTime());
        obs.put("SurvivorCount", survivors.size());

        // Survivors
        List<Map<String, Object>> survivorStates = new ArrayList<>();
        for (IsoPlayer survivor : survivors) {
            survivorStates.add(extractSurvivor(survivor));
        }
        obs.put("Survivors", survivorStates);

        // Environment
        if (!survivors.isEmpty()) {
            IsoPlayer primary = survivors.get(0);
            obs.put("Weather", extractWeather());
            obs.put("VisibleZombies", extractNearbyZombies(primary, 30));
            obs.put("NearbyItems", extractNearbyItems(primary, 15));
        }

        return obs;
    }

    /**
     * Extract single survivor state.
     */
    public Map<String, Object> extractSurvivor(IsoPlayer player) {
        Map<String, Object> state = new HashMap<>();

        state.put("Id", "Player" + player.getPlayerNum());
        state.put("Name", player.getDisplayName());

        // Position
        Map<String, Object> pos = new HashMap<>();
        pos.put("X", player.getX());
        pos.put("Y", player.getY());
        pos.put("Z", player.getZ());
        state.put("Position", pos);

        // Survival stats (0-1 scale, inverted where needed)
        try {
            state.put("Health", player.getHealth());
            if (player.getStats() != null) {
                state.put("Hunger", 1.0f - player.getStats().getHunger());
                state.put("Thirst", 1.0f - player.getStats().getThirst());
                state.put("Fatigue", 1.0f - player.getStats().getFatigue());
                state.put("Stress", player.getStats().getStress());
            }

            // Infection
            if (player.getBodyDamage() != null) {
                state.put("Infected", player.getBodyDamage().IsInfected());
                state.put("Temperature", player.getBodyDamage().getTemperature());
            }
        } catch (Exception e) {
            // Some getters may fail depending on game state
        }

        // Combat status
        state.put("IsZombie", player.isZombie());
        state.put("IsDead", player.isDead());

        // Equipment
        try {
            if (player.getPrimaryHandItem() != null) {
                state.put("PrimaryWeapon", player.getPrimaryHandItem().getDisplayName());
            }
            if (player.getSecondaryHandItem() != null) {
                state.put("SecondaryWeapon", player.getSecondaryHandItem().getDisplayName());
            }
        } catch (Exception e) {
            // ignore
        }

        return state;
    }

    /**
     * Extract nearby zombies.
     */
    public List<Map<String, Object>> extractNearbyZombies(IsoPlayer player, int radius) {
        List<Map<String, Object>> zombies = new ArrayList<>();

        try {
            IsoCell cell = IsoWorld.instance.CurrentCell;
            if (cell == null) return zombies;

            // Get zombie list more efficiently
            for (int i = 0; i < cell.getZombieList().size() && zombies.size() < 100; i++) {
                IsoZombie zombie = cell.getZombieList().get(i);
                float dist = player.DistTo(zombie);
                if (dist <= radius) {
                    Map<String, Object> z = new HashMap<>();
                    z.put("Id", "Zombie" + zombie.getID());
                    z.put("X", zombie.getX());
                    z.put("Y", zombie.getY());
                    z.put("Z", zombie.getZ());
                    z.put("Distance", dist);
                    z.put("Health", zombie.getHealth());
                    z.put("IsCrawler", zombie.isCrawling());
                    zombies.add(z);
                }
            }
        } catch (Exception e) {
            System.err.println("[GameRL] Error extracting zombies: " + e.getMessage());
        }

        return zombies;
    }

    /**
     * Extract nearby items on ground.
     */
    public List<Map<String, Object>> extractNearbyItems(IsoPlayer player, int radius) {
        List<Map<String, Object>> items = new ArrayList<>();

        try {
            IsoCell cell = IsoWorld.instance.CurrentCell;
            if (cell == null) return items;

            int px = (int) player.getX();
            int py = (int) player.getY();
            int pz = (int) player.getZ();

            for (int x = px - radius; x <= px + radius && items.size() < 50; x++) {
                for (int y = py - radius; y <= py + radius && items.size() < 50; y++) {
                    IsoGridSquare square = cell.getGridSquare(x, y, pz);
                    if (square == null) continue;

                    for (int i = 0; i < square.getObjects().size(); i++) {
                        IsoObject obj = square.getObjects().get(i);
                        if (obj instanceof IsoWorldInventoryObject) {
                            IsoWorldInventoryObject worldItem = (IsoWorldInventoryObject) obj;
                            Map<String, Object> item = new HashMap<>();
                            item.put("Id", "Item" + worldItem.getID());
                            if (worldItem.getItem() != null) {
                                item.put("Name", worldItem.getItem().getDisplayName());
                                item.put("Type", worldItem.getItem().getType());
                            }
                            item.put("X", x);
                            item.put("Y", y);
                            item.put("Z", pz);
                            items.add(item);
                        }
                    }
                }
            }
        } catch (Exception e) {
            System.err.println("[GameRL] Error extracting items: " + e.getMessage());
        }

        return items;
    }

    /**
     * Get current game tick.
     */
    private long getCurrentTick() {
        try {
            return (long) (IsoWorld.instance.getWorldAgeHours() * 3600);
        } catch (Exception e) {
            return 0;
        }
    }

    /**
     * Extract game time info.
     */
    private Map<String, Object> extractGameTime() {
        Map<String, Object> time = new HashMap<>();
        try {
            time.put("Day", IsoWorld.instance.getWorld().getGametime());
            time.put("Hour", IsoWorld.instance.getWorld().getGametime() % 24);
        } catch (Exception e) {
            time.put("Day", 0);
            time.put("Hour", 0);
        }
        return time;
    }

    /**
     * Extract weather info.
     */
    private Map<String, Object> extractWeather() {
        Map<String, Object> weather = new HashMap<>();
        weather.put("Condition", "Clear"); // TODO: Get actual weather
        weather.put("Temperature", 20.0f);
        return weather;
    }

    /**
     * Compute state hash for determinism verification.
     */
    public String computeStateHash(List<IsoPlayer> survivors) {
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");

            for (IsoPlayer player : survivors) {
                md.update(("P" + player.getPlayerNum()).getBytes());
                md.update(Float.toString(player.getX()).getBytes());
                md.update(Float.toString(player.getY()).getBytes());
                md.update(Float.toString(player.getHealth()).getBytes());
            }

            byte[] hash = md.digest();
            StringBuilder sb = new StringBuilder();
            for (byte b : hash) {
                sb.append(String.format("%02x", b));
            }
            return sb.toString().substring(0, 16); // Truncate for brevity
        } catch (Exception e) {
            return "error";
        }
    }

    /**
     * Get observation space schema.
     */
    public Map<String, Object> getObservationSpace(String agentType) {
        Map<String, Object> space = new HashMap<>();
        space.put("type", "dict");

        Map<String, Object> subspaces = new HashMap<>();
        subspaces.put("Tick", Map.of("type", "int"));
        subspaces.put("SurvivorCount", Map.of("type", "int", "min", 0, "max", 4));
        subspaces.put("Survivors", Map.of("type", "sequence", "max_length", 4));
        subspaces.put("VisibleZombies", Map.of("type", "sequence", "max_length", 100));
        subspaces.put("NearbyItems", Map.of("type", "sequence", "max_length", 50));

        space.put("spaces", subspaces);
        return space;
    }
}
