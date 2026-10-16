//============ Copyright (c) Valve Corporation, All rights reserved. ============
//
// Trimmed from openvr samples/drivers/utils/vrmath/vrmath.h to the helpers
// this driver calls. The functions are inline, not static, so a source
// that calls only some of them builds without C4505 at /W4.
#pragma once

#include "openvr_driver.h"
#include <cmath>

#ifndef M_PI
#define M_PI 3.1415926535
#endif

#define DEG_TO_RAD( degrees ) ( ( degrees )*M_PI / 180.0 )

// 3x3 or 3x4 matrix
template < class T >
vr::HmdQuaternion_t HmdQuaternion_FromMatrix( const T &matrix )
{
	vr::HmdQuaternion_t q{};

	q.w = sqrt( fmax( 0, 1 + matrix.m[ 0 ][ 0 ] + matrix.m[ 1 ][ 1 ] + matrix.m[ 2 ][ 2 ] ) ) / 2;
	q.x = sqrt( fmax( 0, 1 + matrix.m[ 0 ][ 0 ] - matrix.m[ 1 ][ 1 ] - matrix.m[ 2 ][ 2 ] ) ) / 2;
	q.y = sqrt( fmax( 0, 1 - matrix.m[ 0 ][ 0 ] + matrix.m[ 1 ][ 1 ] - matrix.m[ 2 ][ 2 ] ) ) / 2;
	q.z = sqrt( fmax( 0, 1 - matrix.m[ 0 ][ 0 ] - matrix.m[ 1 ][ 1 ] + matrix.m[ 2 ][ 2 ] ) ) / 2;

	q.x = copysign( q.x, matrix.m[ 2 ][ 1 ] - matrix.m[ 1 ][ 2 ] );
	q.y = copysign( q.y, matrix.m[ 0 ][ 2 ] - matrix.m[ 2 ][ 0 ] );
	q.z = copysign( q.z, matrix.m[ 1 ][ 0 ] - matrix.m[ 0 ][ 1 ] );

	return q;
}

inline vr::HmdQuaternion_t HmdQuaternion_FromEulerAngles(double roll, double pitch, double yaw) {
  double cr = cos(roll * 0.5);
  double sr = sin(roll * 0.5);
  double cp = cos(pitch * 0.5);
  double sp = sin(pitch * 0.5);
  double cy = cos(yaw * 0.5);
  double sy = sin(yaw * 0.5);

  vr::HmdQuaternion_t q;
  q.w = cr * cp * cy + sr * sp * sy;
  q.x = cr * sp * cy + sr * cp * sy;
  q.y = cr * cp * sy - sr * sp * cy;
  q.z = sr * cp * cy - cr * sp * sy;

  return q;
}

inline vr::HmdQuaternion_t operator-( const vr::HmdQuaternion_t &q )
{
	return { q.w, -q.x, -q.y, -q.z };
}

inline vr::HmdQuaternion_t operator*( const vr::HmdQuaternion_t &lhs, const vr::HmdQuaternion_t &rhs )
{
	return {
		lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z,
		lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
		lhs.w * rhs.y - lhs.x * rhs.z + lhs.y * rhs.w + lhs.z * rhs.x,
		lhs.w * rhs.z + lhs.x * rhs.y - lhs.y * rhs.x + lhs.z * rhs.w,
	};
}

inline vr::HmdVector3_t HmdVector3_From34Matrix( const vr::HmdMatrix34_t &matrix )
{
	return { matrix.m[ 0 ][ 3 ], matrix.m[ 1 ][ 3 ], matrix.m[ 2 ][ 3 ] };
}

inline vr::HmdVector3_t operator+( const vr::HmdVector3_t &vec1, const vr::HmdVector3_t &vec2 )
{
	return { vec1.v[ 0 ] + vec2.v[ 0 ], vec1.v[ 1 ] + vec2.v[ 1 ], vec1.v[ 2 ] + vec2.v[ 2 ] };
}

inline vr::HmdVector3_t operator*( const vr::HmdVector3_t &vec, const vr::HmdQuaternion_t &q )
{
	const vr::HmdQuaternion_t qvec = { 0.0, vec.v[ 0 ], vec.v[ 1 ], vec.v[ 2 ] };

	const vr::HmdQuaternion_t qResult = (q * qvec) * (-q);

	return { static_cast< float >( qResult.x ), static_cast< float >( qResult.y ), static_cast< float >( qResult.z ) };
}