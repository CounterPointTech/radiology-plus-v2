using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFI_Exam_Monitoring_Service
{
    public class StudyData
    {
        public string source { get; set; }
        public string patient_id { get; set; }
        public string accession { get; set; }
        public string facility { get; set; }
        public string patient_group { get; set; }
        public string modality { get; set; }
        public string anatomical_area { get; set; }
        public string patient_last_name { get; set; }
        public string patient_first_name { get; set; }
        public DateTime birth_date { get; set; }
        public string custom_3 { get; set; }
        public DateTime study_date { get; set; }
        public DateTime date_added { get; set; }
        public string status { get; set; }
        public string free_field_1 { get; set; }
        public string free_field_2 { get; set; }
        public string free_field_3 { get; set; }
        public string free_field_4 { get; set; }
        public string free_field_5 { get; set; }
        public string free_field_6 { get; set; }
    }
}
