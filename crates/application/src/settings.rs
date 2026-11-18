use std::collections::BTreeMap;
use std::path::PathBuf;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserSettings {
    pub first_name: String,
    pub last_name: String,
    pub address: Option<String>,
    pub department: Option<String>,
    pub personnel_number: Option<String>,
    pub class_name: Option<String>,
    pub annual_entitlement_half_units: u32,
    pub workday_mask: u8,
    pub federal_state_code: Option<String>,
    pub student_mode: bool,
    pub vocational_school_week: [u8; 7],
    pub include_optional_holidays: bool,
    pub export_path: Option<PathBuf>,
    pub calendar_colors: BTreeMap<String, String>,
}

impl Default for UserSettings {
    fn default() -> Self {
        Self {
            first_name: String::new(),
            last_name: String::new(),
            address: None,
            department: None,
            personnel_number: None,
            class_name: None,
            annual_entitlement_half_units: 0,
            workday_mask: 0b0001_1111,
            federal_state_code: None,
            student_mode: false,
            vocational_school_week: [0; 7],
            include_optional_holidays: false,
            export_path: None,
            calendar_colors: BTreeMap::new(),
        }
    }
}

pub trait SettingsRepository {
    type Error;

    fn load_settings(&self) -> Result<Option<UserSettings>, Self::Error>;
    fn save_settings(&self, settings: &UserSettings) -> Result<(), Self::Error>;
}
