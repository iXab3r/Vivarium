pub fn add(a: i32, b: i32) -> i32 {
    a + b
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arithmetic_survives_archiving() {
        assert_eq!(add(2, 2), 4);
    }

    #[test]
    fn filesystem_is_writable() {
        let path = std::env::temp_dir().join("vivarium-payload-rust.txt");
        std::fs::write(&path, "hello from a pristine machine").unwrap();
        assert!(std::fs::read_to_string(&path).unwrap().contains("pristine"));
        let _ = std::fs::remove_file(&path);
    }
}
