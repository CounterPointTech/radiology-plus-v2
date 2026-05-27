with tmp_data as (
select 
pa.patient_id,
st.accession,
st.id as study_id,
op.procedure_id,
st.custom_1,
case 
when st.custom_1 = u.user_name then (
select phy.physician_id 
from ris.physicians phy 
where phy.user_id = u.user_id 
limit 1
)
else null 
end as rad_id,
u.user_id as user_id
from pacs.studies st
inner join pacs.patients pa on st.patient = pa.id
left outer join shared.users u on st.custom_1 = u.user_name
left outer join ris.orders o on st.accession = o.accession_number 
and pa.patient_id = o.patient_id
left outer join ris.order_procedures op on o.order_id = op.order_id
where coalesce(st.custom_1, '') != ''
and st.custom_1 = u.user_name
and st.status != 1
and op.assigned_physician_id is null
and st.study_date >= now() - interval '7 days'
),
/* auto assign by facility */
/* update RIS */
first_update as (
update ris.order_procedures op
set assigned_physician_id = 4
from ris.modalities m, shared.facilities fa
where 
op.procedure_date_start >= now() - interval '7 days'
and op.modality_id = m.modality_id
and fa.facility_id = m.facility_id
and fa.name like 'longhorn%'
and op.assigned_physician_id is null
returning op.procedure_id
),
/* update PACS */
second_update as (
update pacs.studies sot
set assigned_to = 26,
assigned_to_date = now()
from shared.facilities fa
where sot.facility_id = fa.facility_id
and sot.study_date >= now() - interval '7 days'
and fa.name like 'longhorn%'
and sot.assigned_to is null
returning sot.id
),
/* main auto assign code */
/* update RIS */
third_update as (
update ris.order_procedures op
set assigned_physician_id = (
select phy.physician_id 
from ris.physicians phy 
where phy.user_id = td.user_id 
limit 1
)
from tmp_data td
where op.procedure_id = td.procedure_id
and op.assigned_physician_id is null
returning op.procedure_id
),
/* update PACS */
fourth_update as (
update pacs.studies sot
set assigned_to = td.user_id,
assigned_to_date = now()
from tmp_data td
where sot.id = td.study_id
and assigned_to is null
returning sot.id
),
/* auto verify in PACS code */
fifth_update as (
update pacs.studies st
set status = 3
from shared.users u
where st.assigned_to = u.user_id
and st.custom_3 = 'Ready'
and upper(st.custom_1) = upper(u.user_name)
and st.study_date >= now() - interval '7 days'
and st.status = 0
and st.facility_id != 7591
returning st.id
)
/* auto verify in PACS Longhorn code */
update pacs.studies st
set status = 3
from shared.users u
where 
st.custom_3 = 'Ready'
and st.study_date >= now() - interval '7 days'
and st.status = 0
and st.facility_id = 7591;