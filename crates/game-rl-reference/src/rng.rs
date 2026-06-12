//! Deterministic RNG for world generation (no external dependency)

/// SplitMix64 — tiny, fast, deterministic; used only for world generation.
pub struct SplitMix64 {
    state: u64,
}

impl SplitMix64 {
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9E3779B97F4A7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
        z ^ (z >> 31)
    }

    /// Uniform integer in [lo, hi)
    pub fn range(&mut self, lo: i32, hi: i32) -> i32 {
        debug_assert!(hi > lo);
        let span = (hi - lo) as u64;
        lo + (self.next_u64() % span) as i32
    }

    /// Uniform f32 in [0, 1)
    pub fn unit_f32(&mut self) -> f32 {
        (self.next_u64() >> 40) as f32 / (1u64 << 24) as f32
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_deterministic() {
        let mut a = SplitMix64::new(42);
        let mut b = SplitMix64::new(42);
        for _ in 0..100 {
            assert_eq!(a.next_u64(), b.next_u64());
        }
    }

    #[test]
    fn test_range_bounds() {
        let mut rng = SplitMix64::new(1);
        for _ in 0..1000 {
            let v = rng.range(-5, 64);
            assert!((-5..64).contains(&v));
        }
    }
}
